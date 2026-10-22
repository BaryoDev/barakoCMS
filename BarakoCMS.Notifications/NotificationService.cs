using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Notifications;

internal sealed class NotificationService(
    IDocumentSession session,
    TenantContext tenant,
    IEnumerable<INotificationSink> sinks,
    IOptions<NotificationsOptions> options,
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task<Guid> RaiseAsync(RaiseNotification request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecipientUserId == Guid.Empty)
            throw new ArgumentException("A notification needs a recipient.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Type))
            throw new ArgumentException("A notification needs a type.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("A notification needs a title.", nameof(request));
        if (NotificationRules.HasControlCharacter(request.Type) || NotificationRules.HasControlCharacter(request.Title))
            throw new ArgumentException("A type or title cannot hold control characters.", nameof(request));
        if (NotificationRules.TooLong(request.Type, NotificationRules.MaxTypeLength)
            || NotificationRules.TooLong(request.Title, NotificationRules.MaxTitleLength)
            || NotificationRules.TooLong(request.Body, NotificationRules.MaxBodyLength))
            throw new ArgumentException(
                $"A type is at most {NotificationRules.MaxTypeLength}, a title {NotificationRules.MaxTitleLength} and a body {NotificationRules.MaxBodyLength} characters.",
                nameof(request));
        if (NotificationRules.TooManyPayloadKeys(request.Payload))
            throw new ArgumentException($"A payload holds at most {NotificationRules.MaxPayloadKeys} keys.", nameof(request));
        if (NotificationRules.PayloadTooLarge(request.Payload))
            throw new ArgumentException(
                $"A payload is at most {NotificationRules.MaxPayloadBytes} bytes once serialized.", nameof(request));

        if (!await IsRecipientInTenantAsync(request.RecipientUserId, ct))
            throw new RecipientNotInTenantException(request.RecipientUserId);

        var notification = new Notification
        {
            RecipientUserId = request.RecipientUserId,
            Type = request.Type.Trim(),
            Title = request.Title.Trim(),
            Body = request.Body,
            Payload = request.Payload,
        };

        session.Store(notification);
        await session.SaveChangesAsync(ct);

        var timeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SinkTimeoutSeconds));
        foreach (var sink in sinks)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await sink.DeliverAsync(notification, limit.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Notification sink {Sink} gave up after {Seconds}s for notification {NotificationId}",
                    sink.GetType().Name, timeout.TotalSeconds, notification.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification sink {Sink} failed for notification {NotificationId}",
                    sink.GetType().Name, notification.Id);
            }
        }

        return notification.Id;
    }

    private async Task<bool> IsRecipientInTenantAsync(Guid userId, CancellationToken ct)
    {
        if (tenant.IsDefault)
            return await session.Query<User>().AnyAsync(u => u.Id == userId, ct);

        return await session.Query<Membership>().AnyAsync(
            m => m.UserId == userId && m.TenantSlug == tenant.Slug && m.Status == MembershipStatus.Active, ct);
    }
}
