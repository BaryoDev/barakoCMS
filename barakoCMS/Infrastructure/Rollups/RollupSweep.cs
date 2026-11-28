using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// The daily sweep that marks headers whose rollup values may have moved with the calendar, with no
/// write to say so. A window moves at local midnight, and a full recount closes part A's residual window.
/// </summary>
/// <remarks>
/// Once per local day per tenant it marks every header of every type with a windowed rollup, and on
/// local Mondays every header of every type with any rollup. The drain does the counting. A page is
/// one transaction, and no transaction is held across pages, so a crash leaves the cursor where the
/// last commit put it. The cost is one grouped aggregate per 100 marked headers per rollup.
/// </remarks>
internal static class RollupSweep
{
    public const int PageSize = 500;

    private const string TryLockSql = "select pg_try_advisory_xact_lock(hashtextextended(?, 0))";

    private enum Step
    {
        Stop,
        More,
    }

    /// <summary>
    /// Sweeps one tenant until its day is done, another instance holds the lock, or <paramref name="pages"/> pages are marked.
    /// </summary>
    /// <param name="store">The document store the tenant's sessions are opened from.</param>
    /// <param name="tenant">The tenant slug, or null for the default partition.</param>
    /// <param name="clock">What the local day is read from. Read once, so a sweep that crosses midnight stays on its day.</param>
    /// <param name="pages">A limit on pages marked by this run, for tests that stop a sweep part way. Null is no limit.</param>
    /// <returns>The number of headers marked.</returns>
    public static async Task<int> RunAsync(
        IDocumentStore store, string? tenant, RollupClock clock, CancellationToken ct, int? pages = null)
    {
        var marked = 0;
        for (var page = 0; pages is null || page < pages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var (step, count) = await PageAsync(store, tenant, clock, ct);
            marked += count;
            if (step == Step.Stop)
            {
                break;
            }
        }

        return marked;
    }

    private static async Task<(Step Step, int Marked)> PageAsync(
        IDocumentStore store, string? tenant, RollupClock clock, CancellationToken ct)
    {
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        await session.BeginTransactionAsync(ct);
        var taken = await session.QueryAsync<bool>(TryLockSql, ct, $"barakocms:rollup-sweep:{tenant ?? "(default)"}");
        if (taken.Count == 0 || !taken[0])
        {
            return (Step.Stop, 0);
        }

        var today = await clock.TodayAsync(session, ct);
        var state = await session.LoadAsync<RollupSweepState>(RollupSweepState.Key, ct) ?? new RollupSweepState();
        if (state.Day != today.Date)
        {
            state = new RollupSweepState { Day = today.Date };
        }
        else if (state.Done)
        {
            return (Step.Stop, 0);
        }

        var types = await DueTypesAsync(session, today.Date, ct);
        var current = state.HeaderType is null
            ? types.FirstOrDefault()
            : types.FirstOrDefault(t => string.CompareOrdinal(t, state.HeaderType) >= 0);
        if (current is null)
        {
            state.Done = true;
            session.Store(state);
            await session.SaveChangesAsync(ct);
            return (Step.Stop, 0);
        }

        // A type taken away since the last page: the cursor starts the next one from its beginning.
        var after = string.Equals(current, state.HeaderType, StringComparison.Ordinal) ? state.AfterId : null;
        var ids = await HeaderIdsAsync(session, current, after, ct);
        var now = RollupMarks.Now();

        // Stored in the order writes store them, so a sweep and a drain take the mark rows alike.
        foreach (var id in ids.OrderBy(i => i))
        {
            session.Store(new RollupMark { Id = id, HeaderType = current, MarkedAt = now });
        }

        if (ids.Count > 0)
        {
            await RollupMarks.WakeAsync(session, ct);
        }

        state.HeaderType = current;
        state.AfterId = ids.Count == 0 ? after : ids[^1];
        if (ids.Count < PageSize)
        {
            var next = types.FirstOrDefault(t => string.CompareOrdinal(t, current) > 0);
            state.HeaderType = next;
            state.AfterId = null;
            state.Done = next is null;
        }

        session.Store(state);
        await session.SaveChangesAsync(ct);
        return (state.Done ? Step.Stop : Step.More, ids.Count);
    }

    /// <summary>The header ids of the type after the cursor, in the order of the database, which is the cursor's order.</summary>
    private static async Task<IReadOnlyList<Guid>> HeaderIdsAsync(IDocumentSession session, string type, Guid? after, CancellationToken ct)
    {
        var lowered = type.ToLowerInvariant();
        var query = session.Query<Content>().Where(c => c.ContentType.ToLower() == lowered);
        if (after is { } cursor)
        {
            query = query.Where(c => c.MatchesSql("d.id > ?", cursor));
        }

        return await query.OrderBy(c => c.Id).Take(PageSize).Select(c => c.Id).ToListAsync(ct);
    }

    /// <summary>The header types to sweep on <paramref name="day"/>, in name order.</summary>
    private static async Task<List<string>> DueTypesAsync(IQuerySession session, DateOnly day, CancellationToken ct)
    {
        var monday = day.DayOfWeek == DayOfWeek.Monday;
        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);
        return definitions
            .Where(d => d?.Fields is not null && !string.IsNullOrEmpty(d.Name))
            .Where(d => d.Fields.Any(f => f?.Rollup is not null && (monday || RollupFields.WindowOf(f.Rollup) is not null)))
            .Select(d => d.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
