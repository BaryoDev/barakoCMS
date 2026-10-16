using FastEndpoints;
using Marten;

namespace BarakoCMS.Push.Features;

public sealed class ListSubscriptionsEndpoint(IQuerySession session) : EndpointWithoutRequest
{
    public override void Configure() => Get("/api/push/subscriptions");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var rows = await session.Query<StoredSubscription>()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.LastSeenAt)
            .ToListAsync(ct);

        await Send.OkAsync(
            rows.Select(x => new { id = x.Id, createdAt = x.CreatedAt, lastSeenAt = x.LastSeenAt, userAgent = x.UserAgent }).ToList(),
            ct);
    }
}
