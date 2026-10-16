using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Push.Features;

public sealed class SubscribeRequest
{
    public string? Endpoint { get; set; }
    public string? P256dh { get; set; }
    public string? Auth { get; set; }
}

public sealed class SubscribeEndpoint(IDocumentSession session, PushOptions options)
    : Endpoint<SubscribeRequest>
{
    private const int MaxPerUser = 20;

    public override void Configure() => Post("/api/push/subscriptions");

    public override async Task HandleAsync(SubscribeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!options.IsConfigured)
        {
            await Send.ResultAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            return;
        }

        if (!IsValid(req))
        {
            await Send.ResultAsync(Results.BadRequest(new { error = "Invalid subscription." }));
            return;
        }

        var now = DateTime.UtcNow;
        var row = await session.Query<StoredSubscription>()
            .FirstOrDefaultAsync(x => x.Endpoint == req.Endpoint!, ct);
        if (row is not null && row.UserId != userId && (row.P256dh != req.P256dh || row.Auth != req.Auth))
        {
            await Send.ResultAsync(Results.Conflict(new { error = "Subscription belongs to another user." }));
            return;
        }

        if (row is null)
        {
            row = new StoredSubscription { Id = Guid.NewGuid(), CreatedAt = now };
            await TrimToCapAsync(userId, ct);
        }

        row.UserId = userId;
        row.Endpoint = req.Endpoint!;
        row.P256dh = req.P256dh!;
        row.Auth = req.Auth!;
        row.UserAgent = HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
            ? ua[..Math.Min(ua.Length, 512)]
            : null;
        row.LastSeenAt = now;

        session.Store(row);
        await session.SaveChangesAsync(ct);

        await Send.ResultAsync(Results.Created((string?)null, new { id = row.Id }));
    }

    private async Task TrimToCapAsync(Guid userId, CancellationToken ct)
    {
        var mine = await session.Query<StoredSubscription>()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.LastSeenAt)
            .ToListAsync(ct);
        foreach (var old in mine.Take(Math.Max(0, mine.Count - (MaxPerUser - 1))))
            session.Delete(old);
    }

    private bool IsValid(SubscribeRequest req) =>
        options.IsAllowedEndpoint(req.Endpoint)
        && VapidKeyGenerator.TryDecode(req.P256dh, 65, out var p) && p[0] == 0x04
        && VapidKeyGenerator.TryDecode(req.Auth, 16, out _);
}
