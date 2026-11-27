using System.Text.Json;
using System.Text.Json.Nodes;
using barakoCMS.Core;
using barakoCMS.Core.Validation;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>Recomputes the rollup values of the headers marked in a tenant.</summary>
internal sealed class RollupDrainCommand : ICommand
{
    public string TenantId { get; set; } = string.Empty;
}

/// <remarks>
/// Built from the root container on a worker, so it opens its own sessions for the tenant the
/// command names. A failure is thrown, so the queue retries it with backoff; the marks are still
/// there, because a mark is deleted in the commit that wrote what it asked for. A user write that
/// commits between the drain's read of a header and its own commit makes the drain lose on the
/// header's version: that is no failure, so it is queued again a second later and nothing is logged.
/// </remarks>
internal sealed class RollupDrainCommandHandler(IDocumentStore store, IConfiguration configuration)
    : ICommandHandler<RollupDrainCommand>
{
    public async Task ExecuteAsync(RollupDrainCommand command, CancellationToken ct)
    {
        try
        {
            await RollupDrain.RunAsync(store, configuration, command.TenantId, ct);
        }
        catch (Exception e) when (ConcurrencyConflict.Is(e))
        {
            await RollupDrain.QueueAsync(store, command.TenantId, RollupDrain.ConflictRetry, ct);
        }
    }
}

/// <summary>The drain: marks in, recomputed header values out, one transaction a batch.</summary>
/// <remarks>
/// Idempotent. It recomputes from the entries that count, never from a delta, so a run that died
/// after a commit and a run repeated after a retry leave the same values.
/// </remarks>
internal static class RollupDrain
{
    public const int BatchMarks = 100;

    public const int MaxBatches = 10;

    /// <summary>How long a drain waits before looking again at marks it could not take.</summary>
    public static readonly TimeSpan BlockedRetry = TimeSpan.FromMinutes(1);

    /// <summary>How long a drain waits after a user write got in between its read and its commit.</summary>
    public static readonly TimeSpan ConflictRetry = TimeSpan.FromSeconds(1);

    private sealed record Outcome(int Done, bool Blocked, bool More);

    /// <param name="listener">Added to each batch's session. Only a test passes one, to act between the drain's read and its commit.</param>
    public static async Task RunAsync(
        IDocumentStore store, IConfiguration configuration, string tenant, CancellationToken ct,
        IDocumentSessionListener? listener = null)
    {
        var last = new Outcome(0, false, false);
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            last = await BatchAsync(store, configuration, tenant, listener, ct);
            if (last.Done == 0)
            {
                break;
            }
        }

        // Left over: more marks than this run took, or marks on a type a field change holds.
        var more = last.Done > 0 && last.More;
        if (more || last.Blocked)
        {
            await QueueAsync(store, tenant, more ? TimeSpan.FromSeconds(1) : BlockedRetry, ct);
        }
    }

    internal static async Task QueueAsync(IDocumentStore store, string tenant, TimeSpan wait, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        using (JobEnqueue.On(session))
        {
            await new RollupDrainCommand { TenantId = tenant }.QueueJobAsync(DateTime.UtcNow.Add(wait), ct: ct);
        }

        await session.SaveChangesAsync(ct);
    }

    private static async Task<Outcome> BatchAsync(
        IDocumentStore store, IConfiguration configuration, string tenant, IDocumentSessionListener? listener, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        if (listener is not null)
        {
            session.Listeners.Add(listener);
        }

        await session.BeginTransactionAsync(ct);
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, $"barakocms:rollup-drain:{tenant}");

        var held = await HeldTypesAsync(session, ct);
        var marks = await session.Query<RollupMark>()
            .Where(m => !held.Contains(m.HeaderType.ToLower()))
            .OrderBy(m => m.MarkedAt)
            .Take(BatchMarks)
            .ToListAsync(ct);
        if (marks.Count == 0)
        {
            return new Outcome(0, await session.Query<RollupMark>().AnyAsync(ct), false);
        }

        // Locked, then the running changes read again: a change that started while this waited is seen.
        await FieldChanges.LockTypesSharedAsync(session, marks.Select(m => m.HeaderType), ct);
        held = await HeldTypesAsync(session, ct);
        var taken = marks.Where(m => !held.Contains(m.HeaderType.ToLower())).ToList();

        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        var writer = new ContentWriter(session, new ContentSourcingPolicyService(session), configuration)
        {
            WritesForFieldChange = true,
            WritesFromRollupDrain = true,
        };
        foreach (var group in taken.GroupBy(m => m.HeaderType.ToLowerInvariant()))
        {
            await DrainTypeAsync(session, writer, definitions, group.ToList(), ct);
        }

        foreach (var mark in taken)
        {
            // Only the mark this batch read: one set again while it ran is a newer write to count.
            session.DeleteWhere<RollupMark>(m => m.Id == mark.Id && m.MarkedAt == mark.MarkedAt);
        }

        await session.SaveChangesAsync(ct);
        var blocked = taken.Count < marks.Count
            || (held.Count > 0 && await session.Query<RollupMark>().AnyAsync(m => held.Contains(m.HeaderType.ToLower()), ct));
        return new Outcome(taken.Count, blocked, marks.Count == BatchMarks);
    }

    /// <summary>The lower-cased names of the types a field change is running on.</summary>
    private static async Task<List<string>> HeldTypesAsync(IDocumentSession session, CancellationToken ct) =>
        (await session.Query<FieldChange>().Where(c => c.State == FieldChangeState.Running).ToListAsync(ct))
            .Select(c => c.ContentType.ToLowerInvariant())
            .Distinct()
            .ToList();

    private static async Task DrainTypeAsync(
        IDocumentSession session, ContentWriter writer, IReadOnlyList<ContentTypeDefinition> definitions,
        List<RollupMark> marks, CancellationToken ct)
    {
        var ids = marks.Select(m => m.Id).ToList();
        var header = definitions.FirstOrDefault(d => ContentTypeName.Same(d.Name, marks[0].HeaderType));
        var rollups = header?.Fields.Where(f => f?.Rollup is not null).ToList() ?? [];
        if (header is null || rollups.Count == 0)
        {
            return;
        }

        // The rows are not locked. A header is stored with the version it was read at, so a user
        // write that commits before this batch does makes the batch lose on that version, write
        // nothing and keep its marks. Locking the rows first took the document row before the stream
        // row, the reverse of a user write, and the two could deadlock.
        var values = new Dictionary<FieldDefinition, Dictionary<Guid, object?>>();
        foreach (var rollup in rollups)
        {
            var source = SourceOf(definitions, rollup);
            if (source.Ok)
            {
                values[rollup] = await RollupAggregate.ValuesAsync(session, rollup, source.Field, ids, ct);
            }
        }

        foreach (var content in await session.LoadManyAsync<Content>(ct, ids))
        {
            // Only entries of the header type: a stored reference can name an entry of another
            // type, and that entry is not this drain's to write.
            if (!ContentTypeName.Same(content.ContentType, header.Name))
            {
                continue;
            }

            foreach (var @event in Events(content, header, rollups, values))
            {
                await writer.AppendAsync(content, @event, ct);
            }
        }
    }

    internal static (bool Ok, FieldDefinition? Field) SourceOf(IReadOnlyList<ContentTypeDefinition> definitions, FieldDefinition rollup)
    {
        var def = rollup.Rollup!;
        var from = definitions.FirstOrDefault(d => ContentTypeName.Same(d.Name, def.From));
        if (from is null)
        {
            return (false, null);
        }

        if (RollupFields.FnOf(def) == "count")
        {
            return (true, null);
        }

        var field = from.Fields.FieldNamed(def.Field);
        return (field is not null, field);
    }

    /// <summary>
    /// One <see cref="ContentFieldChanged"/> for each rollup whose value moved, each carrying the
    /// entry's data as it stands after that change, the last with the computed fields that read a
    /// rollup recomputed. None when nothing moved.
    /// </summary>
    private static IEnumerable<ContentFieldChanged> Events(
        Content content, ContentTypeDefinition header, List<FieldDefinition> rollups,
        Dictionary<FieldDefinition, Dictionary<Guid, object?>> values)
    {
        var moved = rollups
            .Where(r => values.TryGetValue(r, out var v) && v.TryGetValue(content.Id, out var value) && Differs(content.Data, r.Name, value))
            .ToList();
        if (moved.Count == 0)
        {
            yield break;
        }

        var data = new Dictionary<string, object>(content.Data);
        var preview = new Content { Id = content.Id, ContentType = content.ContentType, SearchText = content.SearchText };
        for (var i = 0; i < moved.Count; i++)
        {
            var key = FieldChanges.KeyOf(data, moved[i].Name) ?? moved[i].Name;
            if (values[moved[i]][content.Id] is { } value)
                data[key] = value;
            else
                data.Remove(key);

            var last = i == moved.Count - 1;
            if (last)
            {
                ComputedFields.Apply(header.Fields, data);
            }

            var text = FieldChanges.SearchText(preview, header, data);
            yield return new ContentFieldChanged(
                content.Id, moved[i].Name, new Dictionary<string, object>(data), text, Guid.Empty, DateTime.UtcNow);
        }
    }

    /// <summary>Whether the stored value is not the computed one. A missing value and a null are the same.</summary>
    private static bool Differs(IReadOnlyDictionary<string, object> data, string field, object? computed)
    {
        var key = FieldChanges.KeyOf(data, field);
        var stored = key is null ? null : data[key];
        if (stored is null || computed is null)
        {
            return !(stored is null && computed is null);
        }

        return !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(stored), JsonSerializer.SerializeToNode(computed));
    }
}
