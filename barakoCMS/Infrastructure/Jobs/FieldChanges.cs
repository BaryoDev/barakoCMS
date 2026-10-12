using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>Runs a <see cref="FieldChange"/> on the job queue.</summary>
internal sealed class FieldChangeCommand : ICommand
{
    public string TenantId { get; set; } = string.Empty;

    public Guid ChangeId { get; set; }
}

/// <remarks>
/// Built from the root container on a worker, so it opens its own session for the tenant the
/// command names. A failure is written onto the change and thrown again, so the queue retries it;
/// whatever batches committed before it stay done.
/// </remarks>
internal sealed class FieldChangeCommandHandler(IDocumentStore store, IConfiguration configuration)
    : ICommandHandler<FieldChangeCommand>
{
    public async Task ExecuteAsync(FieldChangeCommand command, CancellationToken ct)
    {
        try
        {
            await FieldChanges.RunAsync(store, configuration, command.TenantId, command.ChangeId, maxBatches: null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await FieldChanges.RecordErrorAsync(store, command.TenantId, command.ChangeId, e, ct);
            throw;
        }
    }
}

/// <summary>The steps of a field change and how each one rewrites a batch of entries.</summary>
/// <remarks>
/// Every step is idempotent over an entry: running a batch twice leaves it as running it once did.
/// So a run that died after an entry's write committed and before the cursor moved does no harm
/// when it is run again, and the cursor and the entries commit in one transaction anyway.
/// </remarks>
internal static class FieldChanges
{
    public const int BatchSize = 200;

    internal enum Work
    {
        CopyToNewName,
        Define,
        DropOldName,
        Convert,
        DropField,
        SearchText,
    }

    /// <summary>The steps a change runs, in order.</summary>
    /// <remarks>
    /// A rename copies every value to the new name while readers still read the old one, switches
    /// the definition, then drops the old name. A remove takes the field off the definition first,
    /// so nobody reads it, then drops the values. A sensitivity change follows the rule its endpoint
    /// always had: raising rebuilds the search text before the definition changes, lowering after.
    /// </remarks>
    public static IReadOnlyList<Work> Plan(FieldChange change) => change.Kind switch
    {
        FieldChangeKind.Rename => [Work.CopyToNewName, Work.Define, Work.DropOldName],
        FieldChangeKind.Convert => [Work.Convert, Work.Define],
        FieldChangeKind.Remove => [Work.Define, Work.DropField],
        FieldChangeKind.Sensitivity => change.DefinitionFirst ? [Work.Define, Work.SearchText] : [Work.SearchText, Work.Define],
        _ => throw new InvalidOperationException($"No plan for a {change.Kind} change."),
    };

    /// <summary>
    /// Stores a new change and, unless <paramref name="queue"/> is false, queues the job that runs it,
    /// both on <paramref name="session"/>, so they commit with the request's other writes or not at
    /// all.
    /// </summary>
    public static async Task StartAsync(IDocumentSession session, FieldChange change, CancellationToken ct, bool queue = true)
    {
        var lowered = change.ContentType.ToLower();
        change.Id = Guid.NewGuid();
        change.State = FieldChangeState.Running;
        change.StartedAt = change.UpdatedAt = DateTimeOffset.UtcNow;
        change.Total = await session.Query<Content>().CountAsync(c => c.ContentType.ToLower() == lowered, ct);
        session.Store(change);

        if (queue)
        {
            await new FieldChangeCommand { TenantId = session.TenantId, ChangeId = change.Id }.QueueJobAsync(ct: ct);
        }
    }

    /// <summary>The change running on a type, if any. Entry writes to the type wait for it.</summary>
    public static Task<FieldChange?> RunningAsync(IQuerySession session, string contentType, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        return session.Query<FieldChange>()
            .FirstOrDefaultAsync(c => c.ContentType.ToLower() == lowered && c.State == FieldChangeState.Running, ct);
    }

    /// <summary>
    /// Runs a change until it completes, or for at most <paramref name="maxBatches"/> batches, and
    /// returns it as it stands.
    /// </summary>
    public static async Task<FieldChange?> RunAsync(
        IDocumentStore store, IConfiguration configuration, string tenant, Guid id, int? maxBatches, CancellationToken ct,
        int batchSize = BatchSize)
    {
        for (var batches = 0; maxBatches is null || batches < maxBatches; batches++)
        {
            await using var session = store.LightweightSession(tenant);
            var change = await session.LoadAsync<FieldChange>(id, ct);
            if (change is null || change.State == FieldChangeState.Completed)
            {
                return change;
            }

            await StepAsync(session, configuration, change, batchSize, ct);
            if (change.State == FieldChangeState.Completed)
            {
                return change;
            }
        }

        await using var read = store.QuerySession(tenant);
        return await read.LoadAsync<FieldChange>(id, ct);
    }

    public static async Task RecordErrorAsync(IDocumentStore store, string tenant, Guid id, Exception error, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        if (await session.LoadAsync<FieldChange>(id, ct) is { } change)
        {
            var text = $"{error.GetType().Name}: {error.Message}";
            change.Error = text.Length > 1000 ? text[..1000] : text;
            change.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(change);
            await session.SaveChangesAsync(ct);
        }
    }

    /// <summary>One batch of the current step, or the step's end, committed with the progress it made.</summary>
    private static async Task StepAsync(
        IDocumentSession session, IConfiguration configuration, FieldChange change, int batchSize, CancellationToken ct)
    {
        var plan = Plan(change);
        var now = DateTimeOffset.UtcNow;
        change.UpdatedAt = now;
        change.Error = null;

        var lowered = change.ContentType.ToLower();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (change.Step >= plan.Count || definition is null)
        {
            change.State = FieldChangeState.Completed;
            change.CompletedAt = now;
            session.Store(change);
            await session.SaveChangesAsync(ct);
            return;
        }

        var work = plan[change.Step];
        if (work == Work.Define)
        {
            Define(definition, change);
            definition.UpdatedAt = now;
            session.Store(definition);
            NextStep(change);
            session.Store(change);
            await session.SaveChangesAsync(ct);
            return;
        }

        var query = session.Query<Content>().Where(c => c.ContentType.ToLower() == lowered);
        if (change.Cursor is { } cursor)
        {
            query = query.Where(c => c.Id > cursor);
        }

        var batch = await query.OrderBy(c => c.Id).Take(batchSize).ToListAsync(ct);
        if (batch.Count == 0)
        {
            NextStep(change);
            session.Store(change);
            await session.SaveChangesAsync(ct);
            return;
        }

        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session), configuration);
        foreach (var content in batch)
        {
            if (Rewrite(work, change, definition, content) is { } @event)
            {
                await writer.AppendAsync(content, @event, ct);
                change.Processed++;
            }
        }

        change.Cursor = batch[^1].Id;
        session.Store(change);
        await session.SaveChangesAsync(ct);
    }

    private static void NextStep(FieldChange change)
    {
        change.Step++;
        change.Cursor = null;
    }

    /// <summary>The event that rewrites one entry for a step, or null when the entry needs nothing.</summary>
    private static object? Rewrite(Work work, FieldChange change, ContentTypeDefinition definition, Content content)
    {
        var data = new Dictionary<string, object>(content.Data);
        var oldKey = KeyOf(data, change.Field);
        var occurredAt = DateTime.UtcNow;

        switch (work)
        {
            case Work.CopyToNewName when oldKey is not null && data[oldKey] is not null && KeyOf(data, change.NewName!) is null:
                data[change.NewName!] = data[oldKey];
                return new ContentFieldChanged(content.Id, change.Field, data, content.SearchText, change.RequestedBy, occurredAt);

            case Work.DropOldName when oldKey is not null && !string.Equals(oldKey, change.NewName, StringComparison.Ordinal):
                if (KeyOf(data, change.NewName!) is null)
                {
                    data[change.NewName!] = data[oldKey];
                }

                data.Remove(oldKey);
                return new ContentFieldChanged(content.Id, change.Field, data, content.SearchText, change.RequestedBy, occurredAt);

            case Work.DropField when oldKey is not null:
                data.Remove(oldKey);
                return new ContentFieldChanged(content.Id, change.Field, data, SearchText(content, definition, data), change.RequestedBy, occurredAt);

            case Work.Convert when oldKey is not null && data[oldKey] is not null:
                var converted = FieldConversion.Convert(data[oldKey], change);
                if (converted is null)
                {
                    data.Remove(oldKey);
                }
                else if (SameJson(converted, data[oldKey]))
                {
                    return null;
                }
                else
                {
                    data[oldKey] = converted;
                }

                return new ContentFieldChanged(content.Id, change.Field, data, SearchText(content, definition, data), change.RequestedBy, occurredAt);

            case Work.SearchText when content.SearchText is not null:
                var rebuilt = SearchText(content, definition, content.Data, change);
                return string.Equals(rebuilt, content.SearchText, StringComparison.Ordinal)
                    ? null
                    : new ContentFieldSensitivityChanged(
                        content.Id, change.Field, change.FromSensitivity ?? SensitivityLevel.Public,
                        change.ToSensitivity ?? SensitivityLevel.Public, rebuilt, change.RequestedBy, occurredAt);

            default:
                return null;
        }
    }

    /// <summary>
    /// The search text the entry's Public fields make, as the sensitivity endpoint always built it,
    /// with the changed field at the level the change takes it to. Null stays null: an entry never
    /// indexed is not indexed here.
    /// </summary>
    private static string? SearchText(
        Content content, ContentTypeDefinition definition, IReadOnlyDictionary<string, object> data, FieldChange? sensitivity = null)
    {
        if (content.SearchText is null)
        {
            return null;
        }

        var publicFields = definition.Fields
            .Where(f => sensitivity is not null && string.Equals(f.Name, sensitivity.Field, StringComparison.OrdinalIgnoreCase)
                ? sensitivity.ToSensitivity == SensitivityLevel.Public
                : f.Sensitivity == SensitivityLevel.Public)
            .Where(f => !InlineImageFields.Is(f.Type))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Join(
            ' ',
            data.Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>What the definition step does to the type, by kind.</summary>
    private static void Define(ContentTypeDefinition definition, FieldChange change)
    {
        var field = definition.Fields.FirstOrDefault(f => string.Equals(f.Name, change.Field, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return;
        }

        switch (change.Kind)
        {
            case FieldChangeKind.Rename:
                field.Name = change.NewName!;
                FieldNames.Rename(definition, change.Field, change.NewName!);
                break;
            case FieldChangeKind.Remove:
                definition.Fields.Remove(field);
                break;
            case FieldChangeKind.Convert:
                FieldConversion.Retype(field, change);
                break;
            case FieldChangeKind.Sensitivity:
                field.Sensitivity = change.ToSensitivity ?? field.Sensitivity;
                field.VisibleToRoles = change.VisibleToRoles ?? new List<string>();
                field.Mask = change.Mask ?? FieldMask.Default;
                break;
        }
    }

    /// <summary>The key the data holds a field under, matched ignoring case as every reader matches it.</summary>
    internal static string? KeyOf(IReadOnlyDictionary<string, object> data, string field) =>
        data.Keys.FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));

    private static bool SameJson(object a, object b) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a), JsonSerializer.SerializeToNode(b));
}
