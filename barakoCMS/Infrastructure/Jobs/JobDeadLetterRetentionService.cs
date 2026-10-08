using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// Removes dead-lettered and cancelled jobs once they have been given up on for longer than
/// <c>Jobs:DeadLetterRetentionDays</c>.
/// </summary>
/// <remarks>
/// The shape of <c>WebhookDeliveryRetentionService</c>: an hourly tick, a short delay after start,
/// one count and one <c>DeleteWhere</c> per partition, and nothing thrown out of the loop. It also
/// waits for <see cref="JobStorageGate"/>, the same as the workers, so it never creates the jobs
/// table outside the schema apply.
///
/// The hourly purge FastEndpoints runs deletes completed jobs and never touches a dead letter, so
/// before this a dead letter stayed forever. It is counted from <see cref="JobRecord.CompletedAt"/>,
/// which is set when a job gives up. A dead letter stored before that was set has only its
/// <see cref="JobRecord.CreatedAt"/>, which is earlier, so such a row goes a little sooner, never later.
///
/// Zero or less keeps them forever, the reading the other retention settings use.
/// </remarks>
internal sealed class JobDeadLetterRetentionService(
    IDocumentStore store,
    IConfiguration config,
    JobOptions options,
    JobStorageGate gate,
    ILogger<JobDeadLetterRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.DeadLetterRetentionDays <= 0)
        {
            logger.LogInformation(
                "{Key} is zero or less, so dead-lettered and cancelled jobs are kept forever.",
                JobOptions.DeadLetterRetentionDaysKey);
            return;
        }

        try
        {
            await gate.WaitAsync(stoppingToken);
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
                var removed = await SweepAllTenantsAsync(DateTime.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Job dead letter retention removed {Count} job(s)", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during the job dead letter retention sweep");
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

    public async Task<int> SweepAllTenantsAsync(DateTime nowUtc, CancellationToken ct)
    {
        var removed = 0;
        var fromRegistry = TenantPartitions.ListsFromRegistry(config);

        foreach (var tenantId in await TenantPartitions.ListAsync(store, config, PartitionsWithJobsSql, ct))
        {
            await using var session = store.LightweightSession(tenantId);

            if (fromRegistry && !await session.Query<JobRecord>().AnyAsync(ct)) continue;

            removed += await SweepTenantAsync(session, nowUtc, options.DeadLetterRetentionDays, ct);
        }

        return removed;
    }

    private const string PartitionsWithJobsSql =
        "select distinct tenant_id from public.mt_doc_jobs";

    /// <summary>
    /// Deletes the dead letters given up on before the window in one partition. Pure over the
    /// session, so a test drives it without the timer.
    /// </summary>
    /// <returns>How many jobs were removed.</returns>
    public static async Task<int> SweepTenantAsync(IDocumentSession session, DateTime nowUtc, int days, CancellationToken ct)
    {
        if (days <= 0) return 0;

        var cutoff = nowUtc.AddDays(-days);

        var due = await session.Query<JobRecord>().CountAsync(r => r.State == JobState.DeadLettered
            && ((r.CompletedAt != null && r.CompletedAt < cutoff) || (r.CompletedAt == null && r.CreatedAt < cutoff)), ct);
        if (due == 0) return 0;

        session.DeleteWhere<JobRecord>(r => r.State == JobState.DeadLettered
            && ((r.CompletedAt != null && r.CompletedAt < cutoff) || (r.CompletedAt == null && r.CreatedAt < cutoff)));
        await session.SaveChangesAsync(ct);

        return due;
    }
}
