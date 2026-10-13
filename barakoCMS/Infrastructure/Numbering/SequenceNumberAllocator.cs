using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Numbering;

/// <summary>
/// Takes the next number in its own statement, committed at once, rather than inside the writer's
/// transaction: no duplicates and no long lock, and a write that fails afterwards leaves a gap.
/// </summary>
/// <remarks>
/// The counter is the same row the gapless allocator keeps, read and incremented in one upsert, so
/// it is ordered and never hands a number out twice. The row is locked only while that statement
/// runs, which is what a Postgres sequence would give, without creating one per tenant and series.
/// </remarks>
internal sealed class SequenceNumberAllocator(IDocumentStore store, TimeProvider clock) : INumberAllocator
{
    public Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, CancellationToken ct = default) =>
        NextAsync(session, series, clock.GetUtcNow(), ct);

    public async Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(series);
        series.Validate();

        var period = series.PeriodFor(at);
        var value = await NumberCounters.AddAsync(store, session.TenantId, series.Name, period, 1, ct);
        return new AllocatedNumber(series.Name, period, value, series.Format(value, period));
    }
}

/// <summary>
/// Takes numbers a block at a time and hands them out from memory: no duplicates and the least
/// contention, with gaps on a restart and no order between instances.
/// </summary>
internal sealed class HiLoNumberAllocator(IDocumentStore store, TimeProvider clock) : INumberAllocator
{
    /// <summary>How many numbers one instance takes at once.</summary>
    public const int BlockSize = 50;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Block> _blocks = new(StringComparer.Ordinal);

    private sealed class Block
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Next;
        public long Last = -1;
    }

    public Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, CancellationToken ct = default) =>
        NextAsync(session, series, clock.GetUtcNow(), ct);

    public async Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(series);
        series.Validate();

        var period = series.PeriodFor(at);
        var block = _blocks.GetOrAdd($"{session.TenantId}\u001f{series.Name}\u001f{period}", _ => new Block());

        await block.Gate.WaitAsync(ct);
        try
        {
            if (block.Next > block.Last)
            {
                block.Last = await NumberCounters.AddAsync(store, session.TenantId, series.Name, period, BlockSize, ct);
                block.Next = block.Last - BlockSize + 1;
            }

            var value = block.Next++;
            return new AllocatedNumber(series.Name, period, value, series.Format(value, period));
        }
        finally
        {
            block.Gate.Release();
        }
    }
}

/// <summary>The allocator for each strategy, all three registered as singletons.</summary>
internal sealed class NumberStrategies(
    INumberAllocator gapless, SequenceNumberAllocator sequence, HiLoNumberAllocator hiLo) : INumberStrategies
{
    public INumberAllocator For(NumberStrategy strategy) => strategy switch
    {
        NumberStrategy.Sequence => sequence,
        NumberStrategy.HiLo => hiLo,
        _ => gapless,
    };
}

/// <summary>Moves a counter row forward on its own connection, committed at once.</summary>
internal static class NumberCounters
{
    /// <summary>Adds <paramref name="count"/> to the counter and returns its new last value.</summary>
    public static async Task<long> AddAsync(IDocumentStore store, string tenant, string series, string period, int count, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        var schema = store.Options.DatabaseSchemaName;
        var sql =
            $"with x as (insert into {schema}.{NumberCounterTable.Name} as c (tenant_id, series, period, last_value) "
            + "values (?, ?, ?, ?) "
            + "on conflict (tenant_id, series, period) do update set last_value = c.last_value + excluded.last_value "
            + "returning c.last_value) select last_value from x";

        // No transaction is begun, so the statement commits on its own the moment it runs.
        var rows = await session.QueryAsync<long>(sql, ct, tenant, series, period, (long)count);
        return rows.Single();
    }
}
