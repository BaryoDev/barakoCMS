using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Erase;

/// <summary>
/// DELETE /api/roles/{id}/erase: removes a deleted role for good. It only takes a role that is
/// already deleted, so a live one cannot be destroyed in one call.
/// </summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/roles/{id}/erase");
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

        if (role.IsPlatform && !platformCaller)
        {
            await Send.ResponseAsync(new Response { Message = RoleScope.PlatformRoleMessage }, 403, ct);
            return;
        }

        session.Delete(role);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "role.erased", actorId, User.FindFirst("Username")?.Value,
            targetType: "Role", targetId: role.Id.ToString(),
            metadata: RoleAudit.Describe(RoleAudit.Of(role)), ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Message = "Role erased" }, ct);
    }
}
