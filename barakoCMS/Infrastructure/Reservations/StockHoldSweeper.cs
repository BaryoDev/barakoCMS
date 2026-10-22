using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using Marten;

namespace barakoCMS.Infrastructure.Reservations;

/// <summary>
/// Returns the units of lapsed holds on a schedule, so a counter nobody reserves against again does
/// not keep a stale stored balance and a stale open hold.
/// </summary>
/// <remarks>
/// Safe to run on every instance at once: the expiry statement only touches holds still marked held,
/// so a second sweep of the same hold changes nothing.
/// </remarks>
internal sealed class StockHoldSweeper(
    IDocumentStore store, IConfiguration config, IStockReservations stock, ILogger<StockHoldSweeper> logger) : BackgroundService
{
    public const string EnabledKey = "Stock:HoldSweep:Enabled";

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue(EnabledKey, true))
        {
            logger.LogInformation("Stock hold sweep is off. Lapsed holds return on the next reservation of their counter.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during the stock hold sweep");
            }

        }
    }

    /// <returns>The number of holds expired across every partition.</returns>
    public async Task<int> SweepAsync(DateTimeOffset at, CancellationToken ct)
    {
        var sql =
            $"select distinct tenant_id from {store.Options.DatabaseSchemaName}.{StockTables.Holds} "
            + $"where status = 'held' and expires_at is not null and expires_at <= '{at.UtcDateTime:O}'";

        var expired = 0;
        foreach (var tenantId in await TenantPartitions.ListAsync(store, config, sql, ct))
        {
            await using var session = store.LightweightSession(tenantId);
            expired += await stock.ReleaseExpiredAsync(session, at, ct);
            await session.SaveChangesAsync(ct);
        }

        if (expired > 0)
            logger.LogInformation("Stock hold sweep returned {Count} lapsed hold(s)", expired);
        return expired;
    }
}
