using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Features.Sessions.RevokeOthers;

/// <summary>
/// POST /api/me/sessions/revoke-others: ends every session of the caller except the one the request
/// came from. A token with no session (one from before sessions) keeps nothing, since there is no
/// current session to name.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(IDocumentSession session, IMemoryCache cache, TenantContext tenant) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/me/sessions/revoke-others");
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.LogoutPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await SessionRoutes.EndAllAsync(
            session, cache, tenant.Slug, userId, User.FindFirst("Username")?.Value, userId, SessionRoutes.CurrentOf(User), ct);
        await Send.NoContentAsync(ct);
    }
}
