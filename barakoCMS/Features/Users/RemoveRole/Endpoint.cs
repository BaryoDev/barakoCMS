using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.RemoveRole;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Delete("/api/users/{userId}/roles/{roleId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _session.LoadAsync<User>(req.UserId, ct);

        if (user == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        user.RoleIds.Remove(req.RoleId);
        _session.Store(user);
        await _session.SaveChangesAsync(ct);

        // Removing a role narrows the user's access — evict cached decisions so it applies now.
        _permissionResolver.InvalidateUserPermissions(req.UserId);

        await SendOkAsync(new Response { Message = "Role removed from user successfully" }, ct);
    }
}

public class Request
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
