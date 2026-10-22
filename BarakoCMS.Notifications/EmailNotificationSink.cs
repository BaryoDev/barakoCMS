using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using System.Net;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Notifications;

/// <summary>
/// Emails a notification to its recipient when <c>Modules:Notifications:EmailDelivery:Enabled</c> is true.
/// Sent on the host transport: a notification's title and body come from whatever raised it, so a
/// tenant's own transport must not carry it.
/// </summary>
internal sealed class EmailNotificationSink(
    IQuerySession session,
    IEmailService email,
    IOptions<NotificationsOptions> options) : INotificationSink
{
    public async Task DeliverAsync(Notification notification, CancellationToken ct)
    {
        var settings = options.Value.EmailDelivery;
        if (!settings.Enabled) return;
        if (settings.Types.Length > 0 && !settings.Types.Contains(notification.Type, StringComparer.Ordinal)) return;

        var user = await session.LoadAsync<User>(notification.RecipientUserId, ct);
        if (string.IsNullOrWhiteSpace(user?.Email)) return;

        await email.SendEmailAsync(user.Email, notification.Title,
            ToHtml(notification.Body ?? notification.Title), ct);
    }

    private static string ToHtml(string text) =>
        WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>");
}
