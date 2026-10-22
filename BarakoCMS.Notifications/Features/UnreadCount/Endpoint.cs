using FastEndpoints;
using Marten;

namespace BarakoCMS.Notifications.Features.UnreadCount;

internal sealed class UnreadCountResponse
{
    public int Count { get; set; }
}

/// <summary>GET /api/notifications/unread-count. How many of the caller's notifications are unread.</summary>
internal sealed class Endpoint(IQuerySession session) : EndpointWithoutRequest<UnreadCountResponse>
{
    public override void Configure()
    {
        Get("/api/notifications/unread-count");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var me))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var count = await session.Query<Notification>()
            .CountAsync(n => n.RecipientUserId == me && n.ReadAt == null, ct);

        await Send.OkAsync(new UnreadCountResponse { Count = count }, ct);
    }
}
