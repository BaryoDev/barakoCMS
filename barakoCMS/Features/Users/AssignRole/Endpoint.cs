using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignRole;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/users/{userId}/roles");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Both ids are checked before anything is written. This used to fabricate a User on a miss
        // (a synthesized user_{guid}@example.com with no password hash) and answer "Role assigned
        // successfully", so a mistyped id left a ghost identity holding the role while the real
        // account still lacked it. The role id was never checked at all, so a mistyped role also
        // reported success and granted nothing.
        var user = await _session.LoadAsync<User>(req.UserId, ct);
        if (user == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var role = await _session.LoadAsync<Role>(req.RoleId, ct);
        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!user.RoleIds.Contains(req.RoleId))
        {
            user.RoleIds.Add(req.RoleId);
            _session.Store(user);
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(_session, _tenant.Slug, "user.role.assigned", actorId, User.FindFirst("Username")?.Value,
                targetType: "User", targetId: req.UserId.ToString(), metadata: new() { ["roleId"] = req.RoleId.ToString() }, ct: ct);
            await _session.SaveChangesAsync(ct);

            // This user's effective permissions changed — evict their cached decisions.
            _permissionResolver.InvalidateUserPermissions(req.UserId);
        }

        await Send.OkAsync(new Response { Message = "Role assigned to user successfully" }, ct);
    }
}

internal class Request
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
