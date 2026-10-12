using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Delete;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/roles/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageRoles, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await session.LoadAsync<Role>(req.Id, ct);

        var platformCaller = await RoleScope.IsPlatformCallerAsync(HttpContext, ct);
        if (role == null || (!RoleScope.Reaches(role, tenant.Slug) && !platformCaller))
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
                Message = "Cannot delete a system role: SuperAdmin, Admin, User, or the HR role the seeder created."
            }, 403, ct);
            return;
        }

        if (role.IsPlatform && !platformCaller)
        {
            await Send.ResponseAsync(new Response { Message = RoleScope.PlatformRoleMessage }, 403, ct);
            return;
        }

        // Check referential integrity - ensure no users have this role
        var usersWithRole = await session.Query<User>()
            .AnyAsync(u => u.RoleIds.Contains(req.Id), ct);

        if (usersWithRole)
        {
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete role: it is still assigned to users. Remove the role from all users first."
            }, 409, ct);
            return;
        }

        // A role is also held through a tenant membership, which is the list MembershipRoles
        // .EffectiveRoleIdsAsync unions into the global one. Checking User.RoleIds alone let a role
        // held by every member of a tenant be deleted, leaving each membership with a dangling id
        // that PermissionResolver resolves to nothing and therefore denies.
        var tenantsHoldingRole = (await session.Query<Membership>()
                .Where(m => m.RoleIds.Contains(req.Id))
                .ToListAsync(ct))
            .Select(m => m.TenantSlug)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(slug => slug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tenantsHoldingRole.Count > 0)
        {
            // Named rather than merely refused: "still assigned" with no location is a dead end for
            // whoever has to go and unassign it.
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete role: it is still assigned through memberships in "
                    + $"{string.Join(", ", tenantsHoldingRole)}. Remove the role from those members first."
            }, 409, ct);
            return;
        }

        // A tenant's sign-up role and MFA roles are stored by id too. Deleting one would leave Open
        // sign-up handing out a role that grants nothing, and an MFA rule that matches nobody.
        var roleId = req.Id;
        var tenantsNamingRole = (await session.Query<Tenant>()
                .Where(t => t.SignUpRoleId == roleId || t.SignIn.MfaRoleIds.Contains(roleId))
                .Select(t => t.Slug)
                .ToListAsync(ct))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tenantsNamingRole.Count > 0)
        {
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete role: it is the sign-up or MFA role of "
                    + $"{string.Join(", ", tenantsNamingRole)}. Change those tenants' settings first."
            }, 409, ct);
            return;
        }

        session.Delete(role);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.deleted", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: RoleAudit.Describe(RoleAudit.Of(role)), ct: ct);
        await session.SaveChangesAsync(ct);

        // A deleted role changes effective permissions for its holders — evict cached decisions.
        permissionResolver.InvalidateAllPermissions();

        await Send.OkAsync(new Response
        {
            Message = "Role deleted successfully"
        }, ct);
    }
}
