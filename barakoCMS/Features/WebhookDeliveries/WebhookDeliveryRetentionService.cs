using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.WebhookDeliveries;

/// <summary>
/// Removes webhook delivery rows older than <c>Webhooks:DeliveryLogRetentionDays</c>, and separately
/// clears <see cref="WebhookDelivery.ResponseBody"/> on rows older than
/// <c>Webhooks:ResponseBodyRetentionHours</c>.
/// </summary>
/// <remarks>
/// Same shape as <c>TokenCleanupService</c>: an hourly tick, a short delay after start, one
/// <c>DeleteWhere</c> (or update) per pass, and nothing thrown out of the loop. The one difference
/// is that a delivery is a tenant's document, so the pass visits every partition holding one rather
/// than the default partition a plain scope lands on.
///
/// The two windows are independent and deliberately far apart, the way
/// <c>WorkflowRunRetentionService</c> runs two windows in one service rather than two competing
/// background services. The row is worth keeping for months: "did this workflow's hook fire, and
/// when" is a question worth answering long after the fact. The response body is worth keeping for
/// hours: debugging a webhook a provider is rejecting happens in the same shift it broke in, and the
/// body can hold a credential the provider echoed back (see <c>Models/WebhookDelivery.cs</c>), so
/// the shorter window is also the one a credential should not survive. See issue #607.
///
/// Zero or less keeps a window forever, the reading <c>WorkflowRunRetentionService</c> settled on
/// for the same reason: "0" also reads as "delete immediately", and keeping is the direction a
/// mistake can be recovered from. Each window uses that reading on its own; a deployment can keep
/// the row forever while still clearing bodies hourly, or the other way round.
/// </remarks>
internal sealed class WebhookDeliveryRetentionService : BackgroundService
{
    public const string RetentionDaysKey = "Webhooks:DeliveryLogRetentionDays";
    public const int DefaultRetentionDays = 30;

    public const string ResponseBodyRetentionHoursKey = "Webhooks:ResponseBodyRetentionHours";

    /// <summary>
    /// A day. The debugging value of a response body is measured in hours, not the months the row
    /// itself is worth keeping for, and a day covers a shift plus a weekend on-call reading it the
    /// next business morning without leaving a credential sitting in the log any longer than that.
    /// </summary>
    public const int DefaultResponseBodyRetentionHours = 24;

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

    public int ResponseBodyRetentionHours => _config.GetValue(ResponseBodyRetentionHoursKey, DefaultResponseBodyRetentionHours);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (RetentionDays <= 0 && ResponseBodyRetentionHours <= 0)
        {
            _logger.LogInformation(
                "{RowKey} and {BodyKey} are both zero or less, so webhook deliveries and their response bodies are kept forever.",
                RetentionDaysKey, ResponseBodyRetentionHoursKey);
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
                var (removed, bodiesCleared) = await SweepAllTenantsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Webhook delivery retention removed {Count} row(s)", removed);
                }
                if (bodiesCleared > 0)
                {
                    _logger.LogInformation("Webhook delivery retention cleared {Count} response body/bodies", bodiesCleared);
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

    public async Task<(int RowsRemoved, int BodiesCleared)> SweepAllTenantsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var removed = 0;
        var bodiesCleared = 0;

        foreach (var tenantId in await PartitionsWithDeliveriesAsync(ct))
        {
            await using var session = _store.LightweightSession(tenantId);

            // Bodies are cleared first. A row due for outright deletion this pass has nothing left
            // to clear either way, and clearing before deleting means a row that is due for deletion
            // next sweep instead of this one never sits with an expired body in the meantime.
            bodiesCleared += await ClearExpiredResponseBodiesAsync(session, nowUtc, ResponseBodyRetentionHours, ct);
            removed += await SweepTenantAsync(session, nowUtc, RetentionDays, ct);
        }

        return (removed, bodiesCleared);
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

    /// <summary>Rows per batch, and batches per pass, matching <c>WorkflowRunRetentionService</c>.</summary>
    private const int BodyClearBatchSize = 500;
    private const int MaxBodyClearBatchesPerSweep = 20;

    /// <summary>
    /// Clears <see cref="WebhookDelivery.ResponseBody"/> on rows older than the window in one
    /// partition and stamps <see cref="WebhookDelivery.ResponseBodyClearedAt"/>, leaving every other
    /// field on the row untouched. Pure over the session, so a test drives it without the timer.
    /// </summary>
    /// <remarks>
    /// Filtered on <c>ResponseBody != null</c> so a row already cleared, or one that never had a
    /// body to begin with, is not rewritten on every later pass: once <see cref="WebhookDelivery.ResponseBodyClearedAt"/>
    /// is set it stays set, which is what lets a cleared body be told apart from one that was empty
    /// from the start.
    /// </remarks>
    /// <returns>How many rows had their response body cleared.</returns>
    public static async Task<int> ClearExpiredResponseBodiesAsync(
        IDocumentSession session, DateTimeOffset nowUtc, int hours, CancellationToken ct)
    {
        if (hours <= 0) return 0;

        var cutoff = nowUtc.AddHours(-hours);
        var cleared = 0;

        for (var batch = 0; batch < MaxBodyClearBatchesPerSweep; batch++)
        {
            var due = await session.Query<WebhookDelivery>()
                .Where(d => d.ResponseBody != null && d.CreatedAt < cutoff)
                .OrderBy(d => d.CreatedAt)
                .Take(BodyClearBatchSize)
                .ToListAsync(ct);

            if (due.Count == 0) break;

            foreach (var delivery in due)
            {
                delivery.ResponseBody = null;
                delivery.ResponseBodyClearedAt = nowUtc;
                session.Store(delivery);
            }

            await session.SaveChangesAsync(ct);
            cleared += due.Count;

            if (due.Count < BodyClearBatchSize) break;
        }

        return cleared;
    }
}
