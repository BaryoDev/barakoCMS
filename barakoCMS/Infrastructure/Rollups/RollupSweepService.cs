using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// Wakes every few minutes and runs the rollup sweep for the default partition, when it is served,
/// and for each active tenant. A partition whose sweep fails is logged, and the rest still run.
/// </summary>
/// <remarks>
/// The tick is short so a sweep that stopped part way, or a tenant whose local day just turned,
/// is picked up soon. A tick on a day that is done costs one read per tenant.
/// </remarks>
internal sealed class RollupSweepService(
    IDocumentStore store, IConfiguration configuration, ILogger<RollupSweepService> logger) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAllAsync(stoppingToken);
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One tick: every partition, each with its own clock.</summary>
    /// <returns>The number of headers marked across them.</returns>
    internal async Task<int> SweepAllAsync(CancellationToken ct)
    {
        var marked = 0;
        foreach (var tenant in await PartitionsAsync(ct))
        {
            try
            {
                marked += await RollupSweep.RunAsync(store, tenant, new RollupClock(log: logger), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "The rollup sweep failed for tenant {Tenant}", tenant ?? "(default)");
            }
        }

        return marked;
    }

    /// <summary>The default partition when it is served, then each active tenant, as the scheduled sweep lists them.</summary>
    internal async Task<List<string?>> PartitionsAsync(CancellationToken ct)
    {
        var partitions = new List<string?>();
        if (TenantPartitions.ServesDefaultPartition(configuration))
        {
            partitions.Add(null);
        }

        await using var query = store.QuerySession();
        var tenants = await query.Query<Tenant>().ToListAsync(ct);
        partitions.AddRange(tenants.Where(t => t.Status == TenantStatus.Active).Select(t => (string?)t.Slug));
        return partitions.Distinct().ToList();
    }
}
