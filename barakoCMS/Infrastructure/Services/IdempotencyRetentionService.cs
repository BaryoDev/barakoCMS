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
    public const int MaxBatchesPerSweep = 20;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
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
            try
            {
                await TrySweepAsync(DateTime.UtcNow, stoppingToken);
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
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One sweep, if no other instance is already running one.</summary>
    /// <returns>The records removed, or null when another instance held the lock.</returns>
    public async Task<int?> TrySweepAsync(DateTime nowUtc, CancellationToken ct)
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
            var removed = await SweepAsync(session, nowUtc, options.KeyLifetime, ct);
            if (removed > 0)
            {
                logger.LogInformation("Idempotency key retention removed {Count} record(s)", removed);
            }
            return removed;
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SweepLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <summary>Deletes the records past the window, in batches. Pure over the session, so a test drives it.</summary>
    public static async Task<int> SweepAsync(
        IDocumentSession session, DateTime nowUtc, TimeSpan lifetime, CancellationToken ct)
    {
        var cutoff = nowUtc - lifetime;
        var removed = 0;

        for (var batch = 0; batch < MaxBatchesPerSweep; batch++)
        {
            var due = await session.Query<IdempotencyRecord>()
                .Where(r => r.CreatedAt <= cutoff)
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Key)
                .Take(BatchSize)
                .ToListAsync(ct);

            if (due.Count == 0) break;

            foreach (var key in due)
            {
                session.Delete<IdempotencyRecord>(key);
            }

            await session.SaveChangesAsync(ct);
            removed += due.Count;

            if (due.Count < BatchSize) break;
        }

        return removed;
    }
}
