using FastEndpoints;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Push.Features;

public sealed class SubscribeRequest
{
    public string Endpoint { get; set; } = string.Empty;
    public string P256dh { get; set; } = string.Empty;
    public string Auth { get; set; } = string.Empty;
}

public sealed class SubscribeEndpoint(IDocumentSession session, PushOptions options)
    : Endpoint<SubscribeRequest>
{
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
            .FirstOrDefaultAsync(x => x.Endpoint == req.Endpoint, ct);
        if (row is null)
        {
            row = new StoredSubscription { Id = Guid.NewGuid(), CreatedAt = now };
        }

        row.UserId = userId;
        row.Endpoint = req.Endpoint;
        row.P256dh = req.P256dh;
        row.Auth = req.Auth;
        row.UserAgent = HttpContext.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
            ? ua[..Math.Min(ua.Length, 512)]
            : null;
        row.LastSeenAt = now;

        session.Store(row);
        await session.SaveChangesAsync(ct);

        await Send.ResultAsync(Results.Created((string?)null, new { id = row.Id }));
    }

    private static bool IsValid(SubscribeRequest req) =>
        req.Endpoint.Length is > 0 and < 2048
        && Uri.TryCreate(req.Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && VapidKeyGenerator.TryDecode(req.P256dh, 65, out var p) && p[0] == 0x04
        && VapidKeyGenerator.TryDecode(req.Auth, 16, out _);
}
