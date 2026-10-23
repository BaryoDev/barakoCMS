using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.Outbox;

/// <summary>
/// Sends the messages Publish actions left in the outbox, and retries the ones that did not get
/// through, with a growing wait between attempts.
/// </summary>
/// <remarks>
/// Every send writes a <see cref="WebhookDelivery"/> row, the log the webhook deliveries screen
/// reads, so "did it go?" has one answer for webhooks and published messages. A message that runs out
/// of attempts, or is refused for a reason a retry cannot change, stays in the outbox as
/// <see cref="OutboxStatus.DeadLettered"/>.
///
/// A sender holds a message for <see cref="LeaseDuration"/> before sending it, under optimistic
/// concurrency, so two nodes polling the same table do not both send it.
/// </remarks>
internal sealed class OutboxSender(
    IDocumentStore store,
    IOutboxTransport transport,
    IConfiguration config,
    ILogger<OutboxSender> logger) : BackgroundService
{
    public const string MaxAttemptsKey = "Outbox:MaxAttempts";
    public const int DefaultMaxAttempts = 10;
    public const string PollSecondsKey = "Outbox:PollSeconds";
    public const int DefaultPollSeconds = 5;

    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private const int BatchSize = 50;
    private const string PartitionsWithMessagesSql = "select distinct tenant_id from public.mt_doc_outbox_messages";

    public int MaxAttempts => Math.Max(1, config.GetValue(MaxAttemptsKey, DefaultMaxAttempts));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, config.GetValue(PollSecondsKey, DefaultPollSeconds)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAllTenantsAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError("The outbox sender failed a pass ({Exception})", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> SweepAllTenantsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var sent = 0;

        foreach (var tenantId in await TenantPartitions.ListAsync(store, config, PartitionsWithMessagesSql, ct))
        {
            await using var session = store.LightweightSession(tenantId);
            sent += await DeliverDueAsync(session, now, ct);
        }

        return sent;
    }

    /// <summary>The wait after the <paramref name="attempts"/>th failed send: 30 seconds, doubling, up to an hour.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attempts - 1))));

    /// <summary>
    /// One pass over one partition: sends what is due. Pure over the session, so a test drives it
    /// without the timer.
    /// </summary>
    /// <returns>How many messages were delivered.</returns>
    public async Task<int> DeliverDueAsync(IDocumentSession session, DateTimeOffset now, CancellationToken ct)
    {
        var due = await session.Query<OutboxMessage>()
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now
                && (m.LeasedUntil == null || m.LeasedUntil < now))
            .OrderBy(m => m.NextAttemptAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var delivered = 0;

        foreach (var message in due)
        {
            message.LeasedUntil = now.Add(LeaseDuration);
            session.Update(message);

            try
            {
                await session.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("Concurrency"))
            {
                // Another node took it.
                continue;
            }

            var result = await transport.SendAsync(message, ct);

            message.Attempts++;
            message.LeasedUntil = null;
            session.Store(NewDeliveryRow(message, result, now));

            if (result.Delivered)
            {
                message.Status = OutboxStatus.Delivered;
                message.DeliveredAt = DateTimeOffset.UtcNow;
                message.LastError = null;
                delivered++;
            }
            else
            {
                message.LastError = result.Error;
                if (result.Permanent || message.Attempts >= MaxAttempts)
                {
                    message.Status = OutboxStatus.DeadLettered;
                    logger.LogWarning(
                        "Outbox message {MessageId} was dead-lettered after {Attempts} attempt(s)", message.Id, message.Attempts);
                }
                else
                {
                    message.NextAttemptAt = now.Add(Backoff(message.Attempts));
                }
            }

            session.Update(message);
            await session.SaveChangesAsync(ct);
        }

        return delivered;
    }

    private static WebhookDelivery NewDeliveryRow(OutboxMessage message, OutboxSendResult result, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = message.WorkflowId ?? Guid.Empty,
        RunId = message.RunId,
        Url = Actions.WebhookAction.Redact(message.Url),
        Event = "Publish",
        Attempt = message.Attempts,
        ResponseStatus = result.Status,
        DurationMs = result.DurationMs,
        Error = result.Delivered ? null : result.Error,
        CreatedAt = now,
    };
}
