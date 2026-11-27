using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Rollups;

/// <summary>
/// Wakes every few minutes and runs the rollup sweep for the default partition, when it is served,
/// and for each active tenant. A partition whose sweep fails is logged, and the rest still run. A
/// tick that fails before any partition runs, such as a failed read of the tenant list, is logged,
/// and the next tick runs as usual, so a database blip never stops the host.
/// </summary>
/// <remarks>
/// The tick is short so a sweep that stopped part way, or a tenant whose local day just turned,
/// is picked up soon. A tick on a day that is done costs one read per tenant. A tenant whose site
/// names a zone this server does not know is warned about once a day, not once a tick.
/// </remarks>
internal sealed class RollupSweepService(
    IDocumentStore store, IConfiguration configuration, ILogger<RollupSweepService> logger) : BackgroundService
{
    internal static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    private readonly RollupZoneWarnings _warnings = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await TickAsync(stoppingToken);
                await Task.Delay(Tick, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One tick that throws only on shutdown. Any other failure is logged.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await SweepAllAsync(ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "The rollup sweep tick failed, and the next one runs in {Tick}", Tick);
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
                marked += await RollupSweep.RunAsync(store, tenant, NewClock(), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogError(e, "The rollup sweep failed for tenant {Tenant}", tenant ?? "(default)");
            }
        }

        return marked;
    }

    /// <summary>A clock for one partition's sweep. Every clock shares the service's record of zone warnings.</summary>
    internal RollupClock NewClock(TimeProvider? time = null) => new(time, logger, _warnings);

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
