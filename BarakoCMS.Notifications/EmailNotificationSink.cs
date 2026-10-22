using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Notifications;

/// <summary>Emails a notification to its recipient when <c>Modules:Notifications:EmailDelivery:Enabled</c> is true.</summary>
internal sealed class EmailNotificationSink(
    IQuerySession session,
    IEmailService email,
    TenantContext tenant,
    IOptions<NotificationsOptions> options) : INotificationSink
{
    public async Task DeliverAsync(Notification notification, CancellationToken ct)
    {
        var settings = options.Value.EmailDelivery;
        if (!settings.Enabled) return;
        if (settings.Types.Length > 0 && !settings.Types.Contains(notification.Type, StringComparer.Ordinal)) return;

        var user = await session.LoadAsync<User>(notification.RecipientUserId, ct);
        if (string.IsNullOrWhiteSpace(user?.Email)) return;

        await email.SendForTenantAsync(tenant.Slug, user.Email, notification.Title,
            notification.Body ?? notification.Title, ct);
    }
}
