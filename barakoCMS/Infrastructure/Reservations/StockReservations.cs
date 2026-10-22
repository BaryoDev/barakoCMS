using System.Text.Json.Serialization;
using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Reservations;

/// <summary>
/// Each change is one statement, so Postgres checks the floor and moves the balance with nothing
/// between the read and the write. A second caller waits on the counter's row lock, then evaluates
/// the floor against the committed balance.
/// </summary>
internal sealed class StockReservations(TimeProvider clock) : IStockReservations
{
    private const int MaxReference = 256;
    private static readonly TimeSpan MaxHold = TimeSpan.FromDays(365);
    private static readonly string Counters = StockTables.Counters;
    private static readonly string Holds = StockTables.Holds;
    private static readonly string Movements = StockTables.Movements;

    public async Task<long> ReceiveAsync(IDocumentSession session, string counter, long quantity, string reference = "", long floor = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCounter(counter);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0);
        ValidateReference(reference);
        await BeginAsync(session, ct);

        var sql =
            $"with c as (insert into {Table(session, Counters)} as c (tenant_id, counter, balance, floor) values (?, ?, ?, ?) "
            + "on conflict (tenant_id, counter) do update set balance = c.balance + excluded.balance returning c.balance), "
            + $"m as (insert into {Table(session, Movements)} (tenant_id, id, counter, kind, delta, hold_id, reference, occurred_at) "
            + "select ?, gen_random_uuid(), ?, 'received', ?, null, ?, ? from c returning 1) "
            + "select balance from c";

        var rows = await session.QueryAsync<long>(
            sql, ct, session.TenantId, counter, quantity, floor, session.TenantId, counter, quantity, reference ?? string.Empty, clock.GetUtcNow());
        KeepTransaction(session);
        return rows.Single();
    }

    public async Task<StockReservation> ReserveAsync(IDocumentSession session, string counter, long quantity, TimeSpan? holdFor = null, string reference = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCounter(counter);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0);
        ValidateReference(reference);
        if (holdFor is { } span)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(span, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(span, MaxHold);
        }
        await BeginAsync(session, ct);

        var now = clock.GetUtcNow();
        await ExpireAsync(session, now, counter, ct);

        var holdId = Guid.NewGuid();
        var expiry = holdFor is { } hold ? "?" : "null::timestamptz";
        var sql =
            $"with u as (update {Table(session, Counters)} set balance = balance - ? where tenant_id = ? and counter = ? and balance - ? >= floor returning balance), "
            + $"h as (insert into {Table(session, Holds)} (tenant_id, id, counter, quantity, status, reference, created_at, expires_at) "
            + $"select ?, ?, ?, ?, 'held', ?, ?, {expiry} from u returning 1), "
            + $"m as (insert into {Table(session, Movements)} (tenant_id, id, counter, kind, delta, hold_id, reference, occurred_at) "
            + "select ?, gen_random_uuid(), ?, 'reserved', ?, ?, ?, ? from u returning 1) "
            + "select balance from u";

        var args = new List<object>
        {
            quantity, session.TenantId, counter, quantity,
            session.TenantId, holdId, counter, quantity, reference ?? string.Empty, now,
        };
        if (holdFor is { } until)
            args.Add(now + until);
        args.AddRange([session.TenantId, counter, -quantity, holdId, reference ?? string.Empty, now]);

        var rows = await session.QueryAsync<long>(sql, ct, args.ToArray());
        KeepTransaction(session);

        if (rows.Count == 1)
            return new StockReservation(true, holdId, rows[0]);

        var balance = await session.QueryAsync<long>(
            $"select balance from {Table(session, Counters)} where tenant_id = ? and counter = ?", ct, session.TenantId, counter);
        return new StockReservation(false, null, balance.Count == 0 ? 0 : balance[0]);
    }

    public async Task<bool> ConfirmAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await BeginAsync(session, ct);

        var now = clock.GetUtcNow();
        var sql =
            $"with h as (update {Table(session, Holds)} set status = 'confirmed', settled_at = ? "
            + "where tenant_id = ? and id = ? and status = 'held' and (expires_at is null or expires_at > ?) returning id, counter, quantity), "
            + $"m as (insert into {Table(session, Movements)} (tenant_id, id, counter, kind, delta, hold_id, reference, occurred_at) "
            + "select ?, gen_random_uuid(), counter, 'confirmed', 0, id, '', ? from h returning 1) "
            + "select quantity from h";

        var rows = await session.QueryAsync<long>(sql, ct, now, session.TenantId, holdId, now, session.TenantId, now);
        KeepTransaction(session);
        return rows.Count == 1;
    }

    public async Task<bool> ReleaseAsync(IDocumentSession session, Guid holdId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await BeginAsync(session, ct);

        var now = clock.GetUtcNow();
        var sql =
            $"with h as (update {Table(session, Holds)} set status = 'released', settled_at = ? "
            + "where tenant_id = ? and id = ? and status = 'held' returning id, counter, quantity), "
            + $"c as (update {Table(session, Counters)} c set balance = c.balance + h.quantity from h where c.tenant_id = ? and c.counter = h.counter returning 1), "
            + $"m as (insert into {Table(session, Movements)} (tenant_id, id, counter, kind, delta, hold_id, reference, occurred_at) "
            + "select ?, gen_random_uuid(), counter, 'released', quantity, id, '', ? from h returning 1) "
            + "select quantity from h";

        var rows = await session.QueryAsync<long>(sql, ct, now, session.TenantId, holdId, session.TenantId, session.TenantId, now);
        KeepTransaction(session);
        return rows.Count == 1;
    }

    public async Task<int> ReleaseExpiredAsync(IDocumentSession session, DateTimeOffset? at = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await BeginAsync(session, ct);
        return await ExpireAsync(session, (at ?? clock.GetUtcNow()).ToUniversalTime(), null, ct);
    }

    public async Task<long?> BalanceAsync(IQuerySession session, string counter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCounter(counter);
        var rows = await session.QueryAsync<long>(
            $"select c.balance + coalesce((select sum(h.quantity) from {Table(session, Holds)} h "
            + "where h.tenant_id = c.tenant_id and h.counter = c.counter and h.status = 'held' "
            + "and h.expires_at is not null and h.expires_at <= ?), 0)::bigint "
            + $"from {Table(session, Counters)} c where c.tenant_id = ? and c.counter = ?",
            ct, clock.GetUtcNow(), session.TenantId, counter);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<StockMovement>> MovementsAsync(IQuerySession session, string counter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateCounter(counter);
        var rows = await session.QueryAsync<MovementRow>(
            "select jsonb_build_object('Counter', counter, 'Kind', kind, 'Delta', delta, 'HoldId', hold_id, 'Reference', reference, 'At', occurred_at) "
            + $"from {Table(session, Movements)} where tenant_id = ? and counter = ? order by occurred_at, (case kind when 'received' then 0 when 'reserved' then 1 else 2 end), id",
            ct, session.TenantId, counter);

        return rows
            .Select(r => new StockMovement(r.Counter, Enum.Parse<StockMovementKind>(r.Kind, ignoreCase: true), r.Delta, r.HoldId, r.Reference, r.At))
            .ToList();
    }

    private async Task<int> ExpireAsync(IDocumentSession session, DateTimeOffset at, string? counter, CancellationToken ct)
    {
        var only = counter is null ? string.Empty : " and counter = ?";
        var sql =
            $"with e as (update {Table(session, Holds)} set status = 'expired', settled_at = ? "
            + $"where tenant_id = ? and status = 'held' and expires_at is not null and expires_at <= ?{only} returning id, counter, quantity), "
            + "t as (select counter, sum(quantity)::bigint as q from e group by counter), "
            + $"c as (update {Table(session, Counters)} c set balance = c.balance + t.q from t where c.tenant_id = ? and c.counter = t.counter returning 1), "
            + $"m as (insert into {Table(session, Movements)} (tenant_id, id, counter, kind, delta, hold_id, reference, occurred_at) "
            + "select ?, gen_random_uuid(), counter, 'expired', quantity, id, '', ? from e returning 1) "
            + "select quantity from e";

        var args = new List<object> { at, session.TenantId, at };
        if (counter is not null)
            args.Add(counter);
        args.AddRange([session.TenantId, session.TenantId, at]);

        var rows = await session.QueryAsync<long>(sql, ct, args.ToArray());
        KeepTransaction(session);
        return rows.Count;
    }

    private static string Table(IQuerySession session, string name) =>
        $"{session.DocumentStore.Options.DatabaseSchemaName}.{name}";

    /// <summary>
    /// Without an open transaction each statement would autocommit, and a reservation could no
    /// longer be undone by a failing content save. Marten makes this a no-op when one is open.
    /// </summary>
    private static async Task BeginAsync(IDocumentSession session, CancellationToken ct) => await session.BeginTransactionAsync(ct);

    /// <summary>
    /// Marten commits only when the session has queued work, so a save with no other change would
    /// roll these statements back. An empty DO block returns no result set, so operations queued
    /// after it still read their own results.
    /// </summary>
    private static void KeepTransaction(IDocumentSession session) => session.QueueSqlCommand("do $$ begin end $$");

    private static void ValidateCounter(string counter)
    {
        if (string.IsNullOrWhiteSpace(counter) || counter.Length > 128)
            throw new ArgumentException("A counter name is 1 to 128 characters.", nameof(counter));
    }

    private static void ValidateReference(string? reference)
    {
        if (reference is { Length: > MaxReference })
            throw new ArgumentException($"A reference is at most {MaxReference} characters.", nameof(reference));
    }

    private sealed record MovementRow(
        [property: JsonPropertyName("Counter")] string Counter,
        [property: JsonPropertyName("Kind")] string Kind,
        [property: JsonPropertyName("Delta")] long Delta,
        [property: JsonPropertyName("HoldId")] Guid? HoldId,
        [property: JsonPropertyName("Reference")] string Reference,
        [property: JsonPropertyName("At")] DateTimeOffset At);
}
