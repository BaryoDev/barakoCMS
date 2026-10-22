using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Notifications.Features.List;

/// <summary>GET /api/notifications. The caller's own notifications, newest first, paged.</summary>
internal sealed class Endpoint(IQuerySession session)
    : Endpoint<ListNotificationsRequest, PaginatedResponse<NotificationResponse>>
{
    public override void Configure()
    {
        Get("/api/notifications");
    }

    public override async Task HandleAsync(ListNotificationsRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var me))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var query = session.Query<Notification>().Where(n => n.RecipientUserId == me);
        if (req.UnreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var page = await query.OrderByDescending(n => n.CreatedAt).ToPagedResponseAsync(req, ct);

        await Send.OkAsync(new PaginatedResponse<NotificationResponse>
        {
            Items = page.Items.Select(NotificationResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, ct);
    }
}
