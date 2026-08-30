using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Delete;

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
        Delete("/api/roles/{id}");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await _session.LoadAsync<Role>(req.Id, ct);

        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The same list the API reports IsSystem from, so a client cannot disagree with the server
        // about which roles are deletable.
        if (barakoCMS.Models.SystemRoles.Contains(req.Id))
        {
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete system roles (SuperAdmin, Admin, HR, User)."
            }, 403, ct);
            return;
        }

        // Check referential integrity - ensure no users have this role
        var usersWithRole = await _session.Query<User>()
            .AnyAsync(u => u.RoleIds.Contains(req.Id), ct);

        if (usersWithRole)
        {
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete role: it is still assigned to users. Remove the role from all users first."
            }, 409, ct);
            return;
        }

        _session.Delete(role);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, _tenant.Slug, "role.deleted", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(), metadata: new() { ["name"] = role.Name }, ct: ct);
        await _session.SaveChangesAsync(ct);

        // A deleted role changes effective permissions for its holders — evict cached decisions.
        _permissionResolver.InvalidateAllPermissions();

        await Send.OkAsync(new Response
        {
            Message = "Role deleted successfully"
        }, ct);
    }
}
