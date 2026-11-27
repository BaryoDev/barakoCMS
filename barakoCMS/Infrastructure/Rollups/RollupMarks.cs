using System.Runtime.CompilerServices;
using barakoCMS.Core;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Services;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>A rollup field and the type that declares it.</summary>
internal sealed record RollupRef(string HeaderType, FieldDefinition Field);

/// <summary>
/// What a write needs to know about an entry to tell whether a rollup over it moved. A copy,
/// because the writer applies the event to the entry it holds.
/// </summary>
internal sealed record EntryFacts(
    Guid Id, string ContentType, Dictionary<string, object> Data, ContentStatus Status, SensitivityLevel Sensitivity)
{
    public static EntryFacts? Of(Content? content) => content is null
        ? null
        : new(content.Id, content.ContentType, new Dictionary<string, object>(content.Data ?? []), content.Status, content.Sensitivity);
}

/// <summary>
/// Marks the header entries whose rollup values a write may have changed, in the writer's own
/// transaction, and wakes the drain that recomputes them.
/// </summary>
/// <remarks>
/// The marks and the write commit together or not at all, so a write cannot be counted late for
/// good. Which rollups exist is read once per session and kept for that session only: nothing
/// outlives a request, so a stale copy can never drop a mark.
/// </remarks>
internal static class RollupMarks
{
    private static readonly ConditionalWeakTable<IQuerySession, Task<IReadOnlyList<RollupRef>>> Definitions = new();

    private static readonly ConditionalWeakTable<IDocumentSession, Wake> Wakes = new();

    private static readonly ConditionalWeakTable<IDocumentSession, PendingMarks> Pending = new();

    /// <summary>The rollup fields of every stored type, read once for the session.</summary>
    public static Task<IReadOnlyList<RollupRef>> RollupsAsync(IQuerySession session) =>
        Definitions.GetValue(session, s => ReadAsync(s));

    private static async Task<IReadOnlyList<RollupRef>> ReadAsync(IQuerySession session)
    {
        try
        {
            // A session double built on a mock may hand back no query, and one that cannot read
            // definitions has no rollups, as it has no field change running.
            var query = session.Query<ContentTypeDefinition>();
            if (query is null || await query.ToListAsync(CancellationToken.None) is not { } types)
            {
                return [];
            }

            return types
                .Where(t => t?.Fields is not null)
                .SelectMany(t => t.Fields.Where(f => f?.Rollup is not null).Select(f => new RollupRef(t.Name, f)))
                .ToList();
        }
        catch
        {
            Definitions.Remove(session);
            throw;
        }
    }

    /// <summary>The rollups over entries of <paramref name="contentType"/>.</summary>
    internal static async Task<List<RollupRef>> OverAsync(IQuerySession session, string contentType) =>
        (await RollupsAsync(session)).Where(r => ContentTypeName.Same(r.Field.Rollup!.From, contentType)).ToList();

    /// <summary>
    /// Marks the headers a write to an entry may have changed the rollups of. <paramref name="before"/>
    /// is the committed entry, null on a create, and <paramref name="after"/> the entry as it will be
    /// stored, null on a delete.
    /// </summary>
    public static async Task NoteAsync(IDocumentSession session, EntryFacts? before, Content? after, CancellationToken ct)
    {
        var type = after?.ContentType ?? before?.ContentType;
        if (string.IsNullOrEmpty(type))
        {
            return;
        }

        var headers = new Dictionary<Guid, string>();
        foreach (var rollup in await OverAsync(session, type))
        {
            if (Moved(rollup, before, after))
            {
                Add(headers, rollup.HeaderType, HeaderOf(before?.Data, rollup));
                Add(headers, rollup.HeaderType, HeaderOf(after?.Data, rollup));
            }
        }

        // A header made after its lines were written has nothing counted yet.
        if (before is null && after is not null)
        {
            foreach (var own in (await RollupsAsync(session)).Where(r => ContentTypeName.Same(r.HeaderType, type)))
            {
                headers[after.Id] = own.HeaderType;
            }
        }

        if (headers.Count == 0)
        {
            return;
        }

        // Held until the commit, and stored then in id order across every call of the transaction,
        // as the drain deletes them, so two transactions that touch the same headers in opposite
        // order lock the mark rows alike.
        var now = Now();
        var pending = Pending.GetValue(session, s =>
        {
            var created = new PendingMarks();
            s.Listeners.Add(created);
            return created;
        });
        foreach (var (id, headerType) in headers)
        {
            pending.Marks[id] = new RollupMark { Id = id, HeaderType = headerType, MarkedAt = now };
        }

        await WakeAsync(session, ct);
    }

    /// <summary>The same note from code that cannot await. Only the obsolete synchronous writers use it.</summary>
    public static void Note(IDocumentSession session, EntryFacts? before, Content? after) =>
        NoteAsync(session, before, after, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>The time a mark carries, to the microsecond Postgres keeps, so the drain can match it exactly.</summary>
    internal static DateTimeOffset Now()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }

    private static void Add(Dictionary<Guid, string> headers, string headerType, Guid? id)
    {
        if (id is { } header)
        {
            headers[header] = headerType;
        }
    }

    /// <summary>Whether the entry's write changes what the rollup counts, or which header it counts it for.</summary>
    private static bool Moved(RollupRef rollup, EntryFacts? before, Content? after)
    {
        if (before is null || after is null)
        {
            return true;
        }

        var def = rollup.Field.Rollup!;
        return !Same(ViaText(before.Data, def), ViaText(after.Data, def))
            || (RollupFields.FnOf(def) != "count" && !Same(Raw(before.Data, def.Field), Raw(after.Data, def.Field)))
            || (RollupFields.WindowFieldOf(def) is { } window && !Same(Raw(before.Data, window), Raw(after.Data, window)))
            || (before.Status == ContentStatus.Archived) != (after.Status == ContentStatus.Archived)
            || (before.Status == ContentStatus.Published) != (after.Status == ContentStatus.Published)
            || before.Sensitivity != after.Sensitivity;
    }

    private static object? Raw(IReadOnlyDictionary<string, object>? data, string? field)
    {
        var key = data is null || field is null ? null : FieldChanges.KeyOf(data, field);
        return key is null ? null : data![key];
    }

    private static string? ViaText(IReadOnlyDictionary<string, object>? data, RollupDefinition def) =>
        Raw(data, def.Via)?.ToString()?.ToLowerInvariant();

    private static bool Same(object? a, object? b) =>
        a is null || b is null
            ? a is null && b is null
            : string.Equals(
                System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(b), StringComparison.Ordinal);

    private static Guid? HeaderOf(IReadOnlyDictionary<string, object>? data, RollupRef rollup) =>
        Guid.TryParse(Raw(data, rollup.Field.Rollup!.Via)?.ToString(), out var id) ? id : null;

    /// <summary>Queues one drain for the session's next commit, and no second until that commit has happened.</summary>
    internal static async Task WakeAsync(IDocumentSession session, CancellationToken ct)
    {
        var wake = Wakes.GetValue(session, s =>
        {
            var created = new Wake();
            s.Listeners.Add(created);
            return created;
        });
        if (wake.Queued)
        {
            return;
        }

        wake.Queued = true;
        using (JobEnqueue.On(session))
        {
            await new RollupDrainCommand { TenantId = session.TenantId }.QueueJobAsync(DateTime.UtcNow.AddSeconds(1), ct: ct);
        }
    }

    /// <summary>The marks a session has noted and not yet stored. The commit stores them, sorted by id.</summary>
    private sealed class PendingMarks : DocumentSessionListenerBase
    {
        public Dictionary<Guid, RollupMark> Marks { get; } = [];

        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            foreach (var mark in Marks.Values.OrderBy(m => m.Id))
            {
                session.Store(mark);
            }

            Marks.Clear();
            return Task.CompletedTask;
        }
    }

    /// <summary>Lets a session that commits more than once queue a drain for each commit.</summary>
    private sealed class Wake : DocumentSessionListenerBase
    {
        public bool Queued { get; set; }

        public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
        {
            Queued = false;
            return Task.CompletedTask;
        }
    }
}
