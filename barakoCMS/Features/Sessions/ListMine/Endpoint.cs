using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Sessions.ListMine;

internal sealed class Request : PaginatedRequest { }

/// <summary>GET /api/me/sessions: the caller's live sessions, newest activity first, the current one marked.</summary>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<SessionItem>>
{
    public override void Configure()
    {
        Get("/api/me/sessions");
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.LogoutPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.OkAsync(await SessionRoutes.ListAsync(session, userId, SessionRoutes.CurrentOf(User), req, ct), ct);
    }
}
