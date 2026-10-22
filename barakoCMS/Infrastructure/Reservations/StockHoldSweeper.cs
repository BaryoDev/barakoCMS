using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using Marten;

namespace barakoCMS.Infrastructure.Reservations;

/// <summary>
/// Returns the units of lapsed holds on a schedule, so a counter nobody reserves against again does
/// not keep a stale stored balance and a stale open hold.
/// </summary>
/// <remarks>
/// One instance sweeps per tick, chosen by a Postgres advisory lock. The expiry statement also only
/// touches holds still marked held, so a second sweep of the same hold changes nothing.
/// </remarks>
internal sealed class StockHoldSweeper(
    IDocumentStore store, IConfiguration config, IStockReservations stock, ILogger<StockHoldSweeper> logger) : BackgroundService
{
    public const string EnabledKey = "Stock:HoldSweep:Enabled";

    private const long SweepLockKey = 8_242_026_003L;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue(EnabledKey, true))
        {
            logger.LogInformation("Stock hold sweep is off. Lapsed holds return on the next reservation of their counter.");
            return;
        }

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
                using var pass = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pass.CancelAfter(PassTimeout);
                await TrySweepAsync(DateTimeOffset.UtcNow, pass.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during the stock hold sweep");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Sweeps every partition unless another instance holds the lock.</summary>
    /// <returns>Null when another instance held the lock and this tick did nothing.</returns>
    public async Task<int?> TrySweepAsync(DateTimeOffset at, CancellationToken ct)
    {
        await using var lockConnection = store.Storage.Database.CreateConnection();
        await lockConnection.OpenAsync(ct);

        await using (var acquire = lockConnection.CreateCommand())
        {
            acquire.CommandText = "select pg_try_advisory_lock(@key)";
            acquire.Parameters.AddWithValue("key", SweepLockKey);
            if ((bool?)await acquire.ExecuteScalarAsync(ct) is not true)
                return null;
        }

        try
        {
            return await SweepAsync(at, ct);
        }
        finally
        {
            await using var release = lockConnection.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(@key)";
            release.Parameters.AddWithValue("key", SweepLockKey);
            await release.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    /// <returns>The number of holds expired across every partition.</returns>
    public async Task<int> SweepAsync(DateTimeOffset at, CancellationToken ct)
    {
        var candidates =
            $"select distinct tenant_id from {store.Options.DatabaseSchemaName}.{StockTables.Holds} "
            + "where status = 'held' and expires_at is not null";

        var expired = 0;
        foreach (var tenantId in await TenantPartitions.ListAsync(store, config, candidates, ct))
        {
            try
            {
                expired += await SweepTenantAsync(tenantId, at, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during the stock hold sweep of tenant {TenantId}", tenantId);
            }
        }

        if (expired > 0)
            logger.LogInformation("Stock hold sweep returned {Count} lapsed hold(s)", expired);
        return expired;
    }

    private async Task<int> SweepTenantAsync(string tenantId, DateTimeOffset at, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenantId);
        var due = await session.QueryAsync<int>(
            $"select 1 from {store.Options.DatabaseSchemaName}.{StockTables.Holds} "
            + "where tenant_id = ? and status = 'held' and expires_at is not null and expires_at <= ? limit 1",
            ct, tenantId, at);
        if (due.Count == 0)
            return 0;

        var expired = await stock.ReleaseExpiredAsync(session, at, ct);
        await session.SaveChangesAsync(ct);
        return expired;
    }
}
