using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Caching.Memory;

namespace barakoCMS.Features.Sessions.EndAllForUser;

internal sealed class Request
{
    /// <summary>Bound from the {id} route segment.</summary>
    public Guid Id { get; set; }
}

/// <summary>DELETE /api/users/{id}/sessions: ends every session of a user, for a global SuperAdmin.</summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal sealed class Endpoint(IDocumentSession session, IMemoryCache cache, TenantContext tenant) : Endpoint<Request>
{
    public override void Configure()
    {
        Delete("/api/users/{id}/sessions");
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

        if (await session.LoadAsync<User>(req.Id, ct) is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await SessionRoutes.EndAllAsync(
            session, cache, tenant.Slug, actorId, User.FindFirst("Username")?.Value, req.Id, null, ct);
        await Send.NoContentAsync(ct);
    }
}
