using Marten;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Notifications;

internal sealed class NotificationService(
    IDocumentSession session,
    IEnumerable<INotificationSink> sinks,
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

        foreach (var sink in sinks)
        {
            try
            {
                await sink.DeliverAsync(notification, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Notification sink {Sink} failed for notification {NotificationId}",
                    sink.GetType().Name, notification.Id);
            }
        }

        return notification.Id;
    }
}
