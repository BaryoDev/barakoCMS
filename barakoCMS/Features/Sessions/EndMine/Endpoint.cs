using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Features.Sessions.EndMine;

internal sealed class Request
{
    /// <summary>Bound from the {id} route segment.</summary>
    public Guid Id { get; set; }
}

/// <summary>
/// DELETE /api/me/sessions/{id}: ends one of the caller's own sessions. Ending the current one signs
/// the caller out: this answers 204 and the next request answers 401.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(IDocumentSession session, IMemoryCache cache, TenantContext tenant) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/me/sessions/{id}");
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.LogoutPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var ended = await SessionRoutes.EndAsync(
            session, cache, tenant.Slug, userId, User.FindFirst("Username")?.Value, userId, req.Id, ct);
        if (!ended)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
