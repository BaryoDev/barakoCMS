using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Restore;

/// <summary>POST /api/roles/{id}/restore: brings a deleted role back with the permissions it had.</summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/roles/{id}/restore");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await session.LoadAsync<Role>(req.Id, ct);

        var platformCaller = await RoleScope.IsPlatformCallerAsync(HttpContext, ct);
        if (role == null || role.DeletedAt is null || (!RoleScope.Reaches(role, tenant.Slug) && !platformCaller))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Restoring grants what the role carries to whoever is given it next, so it is held to the
        // rule creating that role is held to, which is stricter than the delete it undoes.
        if ((role.IsPlatform || barakoCMS.Features.Users.PlatformRoles.CarriesPlatformCapability(role)) && !platformCaller)
        {
            await Send.ResponseAsync(new Response { Message = RoleScope.PlatformRoleMessage }, 403, ct);
            return;
        }

        if (await RoleScope.NameTakenAsync(session, role.Name, RoleScope.OwnerOf(role), role.Id, ct))
        {
            await Send.ResponseAsync(new Response { Message = "Another role here has taken this name. Rename that role first." }, 409, ct);
            return;
        }

        role.DeletedAt = null;
        session.Store(role);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.restored", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: RoleAudit.Describe(RoleAudit.Of(role)), ct: ct);
        await session.SaveChangesAsync(ct);

        permissionResolver.InvalidateAllPermissions();

        await Send.OkAsync(new Response { Message = "Role restored successfully" }, ct);
    }
}
