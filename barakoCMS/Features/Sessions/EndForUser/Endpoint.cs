using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Features.Sessions.EndForUser;

internal sealed class Request
{
    /// <summary>Bound from the {id} route segment.</summary>
    public Guid Id { get; set; }

    /// <summary>Bound from the {sessionId} route segment.</summary>
    public Guid SessionId { get; set; }
}

/// <summary>DELETE /api/users/{id}/sessions/{sessionId}: ends one session of a user, for a global SuperAdmin.</summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(IDocumentSession session, IMemoryCache cache, TenantContext tenant) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/users/{id}/sessions/{sessionId}");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.LogoutPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!await PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            AddError(SessionRoutes.RefusedMessage);
            await Send.ErrorsAsync(403, ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        var ended = await SessionRoutes.EndAsync(
            session, cache, tenant.Slug, actorId, User.FindFirst("Username")?.Value, req.Id, req.SessionId, ct);
        if (!ended)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
