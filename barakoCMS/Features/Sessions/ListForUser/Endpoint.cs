using barakoCMS.Features.Users;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Sessions.ListForUser;

internal sealed class Request : PaginatedRequest
{
    /// <summary>Bound from the {id} route segment.</summary>
    public Guid Id { get; set; }
}

/// <summary>
/// GET /api/users/{id}/sessions: a user's live sessions, for a global SuperAdmin. The addresses come
/// from every tenant the user signs in to, which is why a tenant administrator cannot read them.
/// </summary>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, PaginatedResponse<SessionItem>>
{
    public override void Configure()
    {
        Get("/api/users/{id}/sessions");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
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

        await Send.OkAsync(await SessionRoutes.ListAsync(session, req.Id, null, req, ct), ct);
    }
}
