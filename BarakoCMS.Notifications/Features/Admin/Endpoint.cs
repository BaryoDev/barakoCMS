using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using BarakoCMS.Notifications.Features.List;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Notifications.Features.Admin;

internal sealed class AdminListRequest : PaginatedRequest
{
    /// <summary>Only this user's notifications.</summary>
    public Guid? RecipientUserId { get; set; }
}

/// <summary>GET /api/notifications/admin. Every user's notifications in the current tenant, paged.</summary>
internal sealed class Endpoint(IQuerySession session)
    : Endpoint<AdminListRequest, PaginatedResponse<NotificationResponse>>
{
    public override void Configure()
    {
        Get("/api/notifications/admin");
        Definition.RequireCapability(
            NotificationsCapabilities.ManageNotifications, NotificationsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(AdminListRequest req, CancellationToken ct)
    {
        var query = session.Query<Notification>().AsQueryable();
        if (req.RecipientUserId is { } recipient)
        {
            query = query.Where(n => n.RecipientUserId == recipient);
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
