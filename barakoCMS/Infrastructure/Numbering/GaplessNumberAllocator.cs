using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Numbering;

/// <summary>
/// Increments the counter row inside the caller's transaction. Postgres holds the row lock until
/// that transaction ends, so a second writer waits, then reads the committed value: no duplicates.
/// A rollback undoes the increment along with everything else: no gaps.
/// </summary>
internal sealed class GaplessNumberAllocator(TimeProvider clock) : INumberAllocator
{
    public Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, CancellationToken ct = default) =>
        NextAsync(session, series, clock.GetUtcNow(), ct);

    public async Task<AllocatedNumber> NextAsync(IDocumentSession session, NumberSeries series, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(series);
        series.Validate();

        var period = series.PeriodFor(at);

        // Without an open transaction the statement would autocommit and the lock would end with
        // it, which is a sequence, not a gapless counter. Marten makes this a no-op when the session
        // already holds a transaction; the two-numbers-in-one-session test proves that.
        await session.BeginTransactionAsync(ct);

        var schema = session.DocumentStore.Options.DatabaseSchemaName;

        // A CTE so the statement reads as a query to Marten, which only maps rows for a select.
        var sql =
            $"with x as (insert into {schema}.{NumberCounterTable.Name} as c (tenant_id, series, period, last_value) "
            + "values (?, ?, ?, 1) "
            + "on conflict (tenant_id, series, period) do update set last_value = c.last_value + 1 "
            + "returning c.last_value) select last_value from x";

        var rows = await session.QueryAsync<long>(sql, ct, session.TenantId, series.Name, period);
        var value = rows.Single();

        // Marten commits only when the session has queued work, so a save with no other change
        // would roll the increment back and hand the number out twice. Queue a no-op to force it.
        // QueueSqlCommand is a no-data operation, which Marten does not step past when it reads
        // results, so the statement must not return a result set (a select does, one row). An empty
        // DO block returns none, so the operations queued after it read their own results.
        session.QueueSqlCommand("do $$ begin end $$");

        return new AllocatedNumber(series.Name, period, value, series.Format(value, period));
    }
}
