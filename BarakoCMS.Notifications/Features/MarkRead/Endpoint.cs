using FastEndpoints;
using Marten;

namespace BarakoCMS.Notifications.Features.MarkRead;

internal sealed class MarkReadRequest
{
    public Guid Id { get; set; }
}

/// <summary>POST /api/notifications/{id}/read. Marks one of the caller's notifications read.</summary>
/// <remarks>
/// Someone else's notification answers 404, the same as one that does not exist, so the id of
/// another user's notification is not confirmed. Reading it twice keeps the first read time.
/// </remarks>
internal sealed class MarkReadEndpoint(IDocumentSession session) : Endpoint<MarkReadRequest>
{
    public override void Configure()
    {
        Post("/api/notifications/{id}/read");
    }

    public override async Task HandleAsync(MarkReadRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var me))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var notification = await session.LoadAsync<Notification>(req.Id, ct);
        if (notification is null || notification.RecipientUserId != me)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (notification.ReadAt is null)
        {
            notification.ReadAt = DateTimeOffset.UtcNow;
            session.Store(notification);
            await session.SaveChangesAsync(ct);
        }

        await Send.NoContentAsync(ct);
    }
}

internal sealed class MarkAllReadResponse
{
    /// <summary>How many were marked by this call. At the batch size, call again.</summary>
    public int Updated { get; set; }
}

/// <summary>POST /api/notifications/read-all. Marks up to 500 of the caller's unread notifications read.</summary>
internal sealed class MarkAllReadEndpoint(IDocumentSession session) : EndpointWithoutRequest<MarkAllReadResponse>
{
    internal const int BatchSize = 500;

    public override void Configure()
    {
        Post("/api/notifications/read-all");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var me))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var unread = await session.Query<Notification>()
            .Where(n => n.RecipientUserId == me && n.ReadAt == null)
            .OrderBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var notification in unread)
        {
            notification.ReadAt = now;
            session.Store(notification);
        }

        await session.SaveChangesAsync(ct);
        await Send.OkAsync(new MarkAllReadResponse { Updated = unread.Count }, ct);
    }
}
