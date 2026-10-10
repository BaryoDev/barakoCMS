using barakoCMS.Infrastructure.Filters;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Deletes idempotency records older than <c>Idempotency:KeyHours</c>, the window a key is honoured
/// for. See issue #612.
/// </summary>
/// <remarks>
/// The shape of <c>WorkflowRunRetentionService</c>: an hourly tick, a delay after start, an advisory
/// lock so two instances do not delete the same batch, and bounded batches so one pass cannot hold a
/// connection for long. <see cref="IdempotencyRecord"/> is single tenanted, so there is one partition
/// to visit rather than one per tenant.
///
/// The filter already treats an expired key as free, so this is what bounds the table, not what
/// makes a key reusable. A record the sweep has not reached yet behaves as if it were gone.
/// </remarks>
internal sealed class IdempotencyRetentionService(
    IDocumentStore store,
    IdempotencyOptions options,
    ILogger<IdempotencyRetentionService> logger) : BackgroundService
{
    /// <summary>Same family as the other sweep keys, ending in this issue's number.</summary>
    private const long SweepLockKey = 8_242_026_612L;

    public const int BatchSize = 500;

    /// <summary>How long one tick keeps deleting before it lets the connection go.</summary>
    public static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>The wait before the next tick when the last one ran out of budget with rows left.</summary>
    private static readonly TimeSpan CatchUpInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var behind = false;
            try
            {
                behind = (await TrySweepAsync(DateTime.UtcNow, stoppingToken))?.Behind == true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during the idempotency key retention sweep");
            }

            try
            {
                await Task.Delay(behind ? CatchUpInterval : SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One sweep, if no other instance is already running one.</summary>
    /// <returns>What it removed and whether rows were left, or null when another instance held the lock.</returns>
    public async Task<IdempotencySweepResult?> TrySweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var lockConnection = store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", SweepLockKey);

            if ((bool?)await acquire.ExecuteScalarAsync(ct) is not true)
            {
                logger.LogDebug("Another instance is sweeping idempotency keys; skipping this tick.");
                return null;
            }
        }

        try
        {
            await using var session = store.LightweightSession();
            var cutoff = nowUtc - options.KeyLifetime;
            var result = await SweepAsync(session, nowUtc, options.KeyLifetime, BatchSize, SweepBudget, ct);

            if (result.Behind)
            {
                var left = await session.Query<IdempotencyRecord>().CountAsync(r => r.CreatedAt <= cutoff, ct);
                logger.LogWarning(
                    "Idempotency key retention removed {Count} record(s) and ran out of time with {Left} expired record(s) left; the next sweep runs in {Wait}",
                    result.Removed, left, CatchUpInterval);
            }
            else if (result.Removed > 0)
            {
                logger.LogInformation("Idempotency key retention removed {Count} record(s)", result.Removed);
            }

            return result;
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SweepLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes the records past the window in batches until none are left or the budget is spent.
    /// Pure over the session, so a test drives it.
    /// </summary>
    /// <remarks>At least one batch runs whatever the budget, so a tick always makes progress.</remarks>
    public static async Task<IdempotencySweepResult> SweepAsync(
        IDocumentSession session, DateTime nowUtc, TimeSpan lifetime, int batchSize, TimeSpan budget, CancellationToken ct)
    {
        var cutoff = nowUtc - lifetime;
        var removed = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var due = await session.Query<IdempotencyRecord>()
                .Where(r => r.CreatedAt <= cutoff)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Key)
                .Take(batchSize)
                .ToListAsync(ct);

            if (due.Count == 0)
            {
                return new IdempotencySweepResult(removed, Behind: false);
            }

            await DeleteDueAsync(session, due, cutoff, ct);
            removed += due.Count;

            if (due.Count < batchSize)
            {
                return new IdempotencySweepResult(removed, Behind: false);
            }

            if (clock.Elapsed >= budget)
            {
                return new IdempotencySweepResult(removed, Behind: true);
            }
        }
    }

    /// <summary>Deletes the listed keys that are still past the cutoff.</summary>
    /// <remarks>
    /// The cutoff is repeated in the WHERE because a key can be reclaimed between the query that
    /// listed it and this delete. A delete by key alone would remove that fresh claim.
    /// </remarks>
    internal static async Task DeleteDueAsync(
        IDocumentSession session, IReadOnlyList<string> keys, DateTime cutoff, CancellationToken ct)
    {
        var list = keys.ToList();
        session.DeleteWhere<IdempotencyRecord>(r => list.Contains(r.Key) && r.CreatedAt <= cutoff);
        await session.SaveChangesAsync(ct);
    }
}

/// <summary>What one sweep removed, and whether it stopped with expired rows still left.</summary>
internal sealed record IdempotencySweepResult(int Removed, bool Behind);
