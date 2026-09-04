using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.WebhookDeliveries;

/// <summary>
/// Removes webhook delivery rows older than <c>Webhooks:DeliveryLogRetentionDays</c>.
/// </summary>
/// <remarks>
/// Same shape as <c>TokenCleanupService</c>: an hourly tick, a short delay after start, one
/// <c>DeleteWhere</c> per pass, and nothing thrown out of the loop. The one difference is that a
/// delivery is a tenant's document, so the pass visits every partition holding one rather than the
/// default partition a plain scope lands on.
///
/// Zero or less keeps the log forever, the reading <c>WorkflowRunRetentionService</c> settled on
/// for the same reason: "0 days" also reads as "delete immediately", and keeping is the direction
/// a mistake can be recovered from.
/// </remarks>
internal sealed class WebhookDeliveryRetentionService : BackgroundService
{
    public const string RetentionDaysKey = "Webhooks:DeliveryLogRetentionDays";
    public const int DefaultRetentionDays = 30;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IDocumentStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookDeliveryRetentionService> _logger;

    public WebhookDeliveryRetentionService(
        IDocumentStore store, IConfiguration config, ILogger<WebhookDeliveryRetentionService> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    public int RetentionDays => _config.GetValue(RetentionDaysKey, DefaultRetentionDays);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (RetentionDays <= 0)
        {
            _logger.LogInformation("{Key} is zero or less, so webhook deliveries are kept forever.", RetentionDaysKey);
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
                var removed = await SweepAllTenantsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Webhook delivery retention removed {Count} row(s)", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during the webhook delivery retention sweep");
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

    public async Task<int> SweepAllTenantsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var removed = 0;

        foreach (var tenantId in await PartitionsWithDeliveriesAsync(ct))
        {
            await using var session = _store.LightweightSession(tenantId);
            removed += await SweepTenantAsync(session, nowUtc, RetentionDays, ct);
        }

        return removed;
    }

    private async Task<IReadOnlyList<string>> PartitionsWithDeliveriesAsync(CancellationToken ct)
    {
        var partitions = new List<string>();

        await using var conn = _store.Storage.Database.CreateConnection();
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select distinct tenant_id from public.mt_doc_webhook_deliveries";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            partitions.Add(reader.GetString(0));
        }

        return partitions;
    }

    /// <summary>
    /// Deletes the rows older than the window in one partition. Pure over the session, so a test
    /// drives it without the timer.
    /// </summary>
    /// <returns>How many rows were removed.</returns>
    public static async Task<int> SweepTenantAsync(
        IDocumentSession session, DateTimeOffset nowUtc, int days, CancellationToken ct)
    {
        if (days <= 0) return 0;

        var cutoff = nowUtc.AddDays(-days);

        var due = await session.Query<WebhookDelivery>().CountAsync(d => d.CreatedAt < cutoff, ct);
        if (due == 0) return 0;

        session.DeleteWhere<WebhookDelivery>(d => d.CreatedAt < cutoff);
        await session.SaveChangesAsync(ct);

        return due;
    }
}
