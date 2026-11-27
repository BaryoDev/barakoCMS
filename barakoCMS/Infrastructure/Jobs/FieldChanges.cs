using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Features.Blocks.Bindings;
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
        Recompute,
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
        FieldChangeKind.Recompute => [Work.Recompute],
        _ => throw new InvalidOperationException($"No plan for a {change.Kind} change."),
    };

    /// <summary>
    /// How long the job of a change the request runs itself waits before it starts. It finds the
    /// change completed and does nothing, unless the request was cut short.
    /// </summary>
    public static readonly TimeSpan InlineGrace = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Stores a new change and queues the job that runs it, both on <paramref name="session"/>, so
    /// they commit with the request's other writes or not at all. Returns the change already running
    /// on the type instead, storing nothing, when there is one.
    /// </summary>
    /// <remarks>
    /// The check and the store sit under a transaction lock on the type, held until the request's
    /// session commits, so two requests cannot both find the type free and both start a change.
    /// With <paramref name="inlineWhenSmall"/>, a change that fits in one batch is the request's to
    /// run, and its job waits <see cref="InlineGrace"/> as the safety net for a request that stops.
    /// </remarks>
    public static async Task<FieldChange?> StartAsync(IDocumentSession session, FieldChange change, CancellationToken ct, bool inlineWhenSmall = false)
    {
        var lowered = change.ContentType.ToLower();
        await LockTypeAsync(session, change.ContentType, ct);
        if (await RunningAsync(session, change.ContentType, ct) is { } running)
        {
            return running;
        }

        change.Id = Guid.NewGuid();
        change.State = FieldChangeState.Running;
        change.StartedAt = change.UpdatedAt = DateTimeOffset.UtcNow;
        change.Total = await session.Query<Content>().CountAsync(c => c.ContentType.ToLower() == lowered, ct);
        session.Store(change);

        DateTime? executeAfter = inlineWhenSmall && RunsInline(change) ? DateTime.UtcNow.Add(InlineGrace) : null;
        using (JobEnqueue.On(session))
        {
            await new FieldChangeCommand { TenantId = session.TenantId, ChangeId = change.Id }.QueueJobAsync(executeAfter, ct: ct);
        }

        return null;
    }

    /// <summary>
    /// Takes the lock a change to the type starts under, held until the session's transaction ends.
    /// </summary>
    /// <remarks>
    /// A writer that reads what a change rewrites, checks no change is running and then stores, takes
    /// it first: no change can start between its check and its commit, and one that committed before
    /// the lock was taken is found running. Read the type after taking it, not before, or a change
    /// that ran to completion in between is missed. The key names the tenant, as a type is per tenant.
    /// </remarks>
    public static async Task LockTypeAsync(IDocumentSession session, string contentType, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct,
            $"barakocms:field-change:{session.TenantId}:{contentType.ToLower()}");
    }

    /// <summary>
    /// Takes the same lock as <see cref="LockTypeAsync"/> shared, held until the session's transaction ends.
    /// </summary>
    /// <remarks>
    /// For a write that reads the type's lifecycle and then puts an entry in a state. Writers share
    /// the lock, so they do not wait on each other, and a change to the type or its lifecycle takes
    /// the exclusive one and waits for every writer in flight. Read the type after taking it, so a
    /// write never lands in a state a committed change removed. It joins the transaction the session
    /// already has, and opens one only when there is none. Call it before any hook runs: a hook may
    /// take an advisory lock of its own, and a move takes this lock first and then runs the same
    /// hooks, so every writer asks in that order.
    /// </remarks>
    public static async Task LockTypeSharedAsync(IDocumentSession session, string contentType, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock_shared(hashtextextended(?, 0))", ct,
            $"barakocms:field-change:{session.TenantId}:{contentType.ToLower()}");
    }

    /// <summary>The type's definition, read after <see cref="LockTypeSharedAsync"/> so a committed edit is seen.</summary>
    public static async Task<ContentTypeDefinition?> LockAndReadAsync(IDocumentSession session, string contentType, CancellationToken ct)
    {
        await LockTypeSharedAsync(session, contentType, ct);
        return await session.Query<ContentTypeDefinition>().FirstNamedAsync(contentType, ct);
    }

    /// <summary>
    /// Takes <see cref="LockTypeSharedAsync"/> on each type, lowest lowered name first, the order
    /// ContentTypeConfigProvider takes its exclusive locks in. A write over several types asks for
    /// them here before it stages anything, so it cannot wait on a type while holding another that
    /// a change to the types is waiting for.
    /// </summary>
    public static async Task LockTypesSharedAsync(IDocumentSession session, IEnumerable<string> contentTypes, CancellationToken ct)
    {
        foreach (var type in contentTypes.Select(t => t.ToLower()).Distinct().Order(StringComparer.Ordinal))
            await LockTypeSharedAsync(session, type, ct);
    }

    /// <summary>
    /// Takes the exclusive lock on <paramref name="own"/> and the shared lock on each of the others,
    /// all in lowest lowered name first order, so two saves that point at each other cannot wait on
    /// one another's second lock.
    /// </summary>
    public static async Task LockTypesAsync(IDocumentSession session, string own, IEnumerable<string> others, CancellationToken ct)
    {
        var ownKey = own.ToLower();
        var names = others.Select(t => t.ToLower()).Append(ownKey).Distinct().Order(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name == ownKey)
                await LockTypeAsync(session, name, ct);
            else
                await LockTypeSharedAsync(session, name, ct);
        }
    }

    /// <summary>Whether a change is small enough for the request that asked for it to run.</summary>
    public static bool RunsInline(FieldChange change) => change.Total <= BatchSize;

    /// <summary>The refusal for a write or an edit to a type while <paramref name="running"/> runs on it.</summary>
    public static string Busy(FieldChange running) =>
        $"'{running.ContentType}' has a field change running ({running.Id}). Try again when it completes.";

    /// <summary>The change running on a type, if any. Entry writes to the type wait for it.</summary>
    public static Task<FieldChange?> RunningAsync(IQuerySession session, string contentType, CancellationToken ct)
    {
        var lowered = contentType.ToLower();

        // A real session always answers a query; a test double built on a mock may hand back null,
        // and a session that cannot read the changes has none running. ContentUniqueness reads the
        // type's rules the same way, on the same writer path.
        var changes = session.Query<FieldChange>();
        return changes is null
            ? Task.FromResult<FieldChange?>(null)
            : changes.FirstOrDefaultAsync(c => c.ContentType.ToLower() == lowered && c.State == FieldChangeState.Running, ct);
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
            var change = await LoadForBatchAsync(session, id, ct);
            if (change is null || change.State != FieldChangeState.Running)
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

    /// <summary>
    /// The change a batch is about to run, read under the type's lock when it can be abandoned, so
    /// an abandon that committed first is seen and one that comes after waits for the batch.
    /// </summary>
    /// <remarks>
    /// Only a recompute is abandoned, so only its batches take the lock. It is read again after the
    /// lock is taken, since a lightweight session keeps nothing, and a batch that saved a change an
    /// abandon had already moved would otherwise be refused by the concurrency check only.
    /// </remarks>
    private static async Task<FieldChange?> LoadForBatchAsync(IDocumentSession session, Guid id, CancellationToken ct)
    {
        var change = await session.LoadAsync<FieldChange>(id, ct);
        if (change is not { Kind: FieldChangeKind.Recompute, State: FieldChangeState.Running })
        {
            return change;
        }

        await LockTypeAsync(session, change.ContentType, ct);
        return await session.LoadAsync<FieldChange>(id, ct);
    }

    /// <summary>Whether a job for the change is queued or running on the job queue.</summary>
    public static Task<bool> HasLiveJobAsync(IQuerySession session, Guid changeId, CancellationToken ct)
    {
        var id = changeId.ToString();
        return session.Query<JobRecord>().AnyAsync(
            j => (j.State == JobState.Pending || j.State == JobState.Running) && j.CommandJson.Contains(id), ct);
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

        // An account-owned type is not stored in the tenant, and a backfill of its formula runs once
        // per tenant. The other kinds change a type the tenant stores, and write it back.
        var definition = change.Kind == FieldChangeKind.Recompute
            ? await AccountContentTypes.ResolveAsync(session, change.ContentType, ct)
            : await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
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
            await DefineStepAsync(session, definition, change, now, ct);
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

        var rollups = await RollupValuesAsync(session, work, change, definition, batch, ct);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session), configuration) { WritesForFieldChange = true };
        foreach (var content in batch)
        {
            var counted = rollups?.GetValueOrDefault(content.Id);
            await RecountAsync(writer, work, change, definition, content, counted, ct);
            if (Rewrite(work, change, definition, content, work == Work.Recompute ? counted : null) is { } @event)
            {
                await writer.AppendAsync(content, @event, ct);

                // A rename touches an entry twice; it is counted once, when its old name goes.
                if (work != Work.CopyToNewName)
                {
                    change.Processed++;
                }
            }
        }

        change.Cursor = batch[^1].Id;
        session.Store(change);
        await session.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A rollup whose level changes is recounted at the new level in the same batch as the search
    /// text, so it is never left showing what the old level counted.
    /// </summary>
    private static async Task RecountAsync(
        ContentWriter writer, Work work, FieldChange change, ContentTypeDefinition definition, Content content,
        Dictionary<string, object?>? counted, CancellationToken ct)
    {
        if (work == Work.SearchText && counted is not null
            && Rewrite(Work.Recompute, change, definition, content, counted) is { } recount)
        {
            await writer.AppendAsync(content, recount, ct);
        }
    }

    /// <summary>
    /// The rollup values a step writes with the entries: all of the type's for a recompute, and for a
    /// sensitivity change on a rollup field, that field's, counted at the level the change takes it to.
    /// </summary>
    private static async Task<Dictionary<Guid, Dictionary<string, object?>>?> RollupValuesAsync(
        IDocumentSession session, Work work, FieldChange change, ContentTypeDefinition definition, IReadOnlyList<Content> batch, CancellationToken ct)
    {
        var ids = batch.Select(c => c.Id).ToList();
        if (work == Work.Recompute)
        {
            return await Rollups.RollupRecompute.ValuesAsync(session, definition, ids, ct);
        }

        var level = change.Kind == FieldChangeKind.Sensitivity && work == Work.SearchText ? change.ToSensitivity : null;
        return level is null || definition.Fields.All(f => f?.Rollup is null || !string.Equals(f.Name, change.Field, StringComparison.OrdinalIgnoreCase))
            ? null
            : await Rollups.RollupRecompute.ValuesAsync(session, definition, ids, ct, change.Field, level);
    }

    /// <summary>The definition step, with the block bindings a rename carries, committed with the step's progress.</summary>
    private static async Task DefineStepAsync(
        IDocumentSession session, ContentTypeDefinition definition, FieldChange change, DateTimeOffset now, CancellationToken ct)
    {
        Define(definition, change);
        if (change.Kind == FieldChangeKind.Rename)
        {
            // In the step's commit, so a binding never names a field the type does not declare.
            await BindingFields.RenameAsync(session, definition.Name, change.Field, change.NewName!, ct);
        }

        definition.UpdatedAt = now;
        session.Store(definition);
        NextStep(change);
        session.Store(change);
        await session.SaveChangesAsync(ct);
    }

    private static void NextStep(FieldChange change)
    {
        change.Step++;
        change.Cursor = null;
    }

    /// <summary>The event that rewrites one entry for a step, or null when the entry needs nothing.</summary>
    private static object? Rewrite(
        Work work, FieldChange change, ContentTypeDefinition definition, Content content, Dictionary<string, object?>? rollups = null)
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

            case Work.Recompute:
                // The values the entry would be written with now, nothing else touched. A rollup is
                // set before the computed fields that read it.
                SetRollups(data, rollups);
                ComputedFields.Apply(definition.Fields, data);
                return SameJson(data, content.Data)
                    ? null
                    : new ContentFieldChanged(content.Id, change.Field, data, SearchText(content, definition, data), change.RequestedBy, occurredAt);

            default:
                return null;
        }
    }

    private static void SetRollups(Dictionary<string, object> data, Dictionary<string, object?>? rollups)
    {
        foreach (var (name, value) in rollups ?? [])
        {
            var key = KeyOf(data, name) ?? name;
            if (value is null)
                data.Remove(key);
            else
                data[key] = value;
        }
    }

    /// <summary>
    /// The search text the entry's Public fields make, as the sensitivity endpoint always built it,
    /// with the changed field at the level the change takes it to. Null stays null: an entry never
    /// indexed is not indexed here.
    /// </summary>
    internal static string? SearchText(
        Content content, ContentTypeDefinition definition, IReadOnlyDictionary<string, object> data, FieldChange? sensitivity = null)
    {
        if (content.SearchText is null)
        {
            return null;
        }

        var publicFields = barakoCMS.Core.Validation.PublicFields.SearchTextNames(
            definition.Fields, sensitivity?.Field, sensitivity?.ToSensitivity);

        return string.Join(
            ' ',
            data.Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>What the definition step does to the type, by kind.</summary>
    private static void Define(ContentTypeDefinition definition, FieldChange change)
    {
        // Thrown rather than skipped: a change completing without its definition step would report
        // a sensitivity raise or a rename that never happened.
        var field = definition.Fields.FieldNamed(change.Field)
            ?? throw new InvalidOperationException($"'{definition.Name}' has no field '{change.Field}' left to change.");

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

    /// <summary>
    /// The computed fields whose formula is new or different in <paramref name="after"/>. Only a rule
    /// that holds an expression counts, so a type of <c>Fn</c> rules is saved as it always was.
    /// </summary>
    internal static List<string> ChangedFormulas(IEnumerable<FieldDefinition>? before, IEnumerable<FieldDefinition> after)
    {
        string Key(ComputedDefinition? c) => c is null ? string.Empty : JsonSerializer.Serialize(c);

        var held = (before ?? []).Where(f => f is not null).ToList();
        return after
            .Where(f => f is { Computed: { } c } && ComputedFields.HasExpression(c))
            .Where(f => held.FirstOrDefault(h => string.Equals(h.Name, f.Name, StringComparison.OrdinalIgnoreCase)) is not { } was
                || Key(was.Computed) != Key(f.Computed))
            .Select(f => f.Name)
            .ToList();
    }

    /// <summary>
    /// Starts the backfill for a type whose formulas changed, on <paramref name="session"/>, so it
    /// commits with the definition. Null when nothing needs it or it was started; the change already
    /// running on the type when there is one, which the caller refuses.
    /// </summary>
    /// <remarks>A type with no entries has nothing to rewrite, and starts no job.</remarks>
    public static async Task<FieldChange?> RecomputeAsync(
        IDocumentSession session, ContentTypeDefinition definition, IEnumerable<FieldDefinition>? before, Guid requestedBy, CancellationToken ct)
    {
        var changed = ChangedFormulas(before, definition.Fields).Concat(Rollups.RollupRecompute.Changed(before, definition.Fields)).ToList();
        return changed.Count == 0 ? null : await StartRecomputeAsync(session, definition.Name, changed[0], requestedBy, ct);
    }

    /// <summary>Starts a recompute of the type's entries, or null when it has none.</summary>
    private static async Task<FieldChange?> StartRecomputeAsync(
        IDocumentSession session, string contentType, string field, Guid requestedBy, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        if (!await session.Query<Content>().AnyAsync(c => c.ContentType.ToLower() == lowered, ct))
        {
            return null;
        }

        return await StartAsync(
            session, new FieldChange { ContentType = contentType, Field = field, Kind = FieldChangeKind.Recompute, RequestedBy = requestedBy }, ct);
    }

    /// <summary>The key the data holds a field under, matched ignoring case as every reader matches it.</summary>
    internal static string? KeyOf(IReadOnlyDictionary<string, object> data, string field) =>
        data.Keys.FirstOrDefault(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));

    private static bool SameJson(object a, object b) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(JsonSerializer.SerializeToNode(a), JsonSerializer.SerializeToNode(b));
}
