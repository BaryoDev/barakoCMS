using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignRole;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    private static readonly IReadOnlyList<string> LegacyRoles = CapabilityGate.AdminLegacyRoles;

    public override void Configure()
    {
        Post("/api/users/{userId}/roles");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!await PlatformRoles.MayChangeAsync(session, User, configuration, LegacyRoles, ct))
        {
            await Send.ResponseAsync(new Response { Message = PlatformRoles.RefusedMessage }, 403, ct);
            return;
        }

        // Both ids are checked before anything is written. This used to fabricate a User on a miss
        // (a synthesized user_{guid}@example.com with no password hash) and answer "Role assigned
        // successfully", so a mistyped id left a ghost identity holding the role while the real
        // account still lacked it. The role id was never checked at all, so a mistyped role also
        // reported success and granted nothing.
        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var role = await session.LoadAsync<Role>(req.RoleId, ct);
        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A role carrying a platform capability (SuperAdmin, or a custom role with manage_roles and
        // the like) is granted only by a SuperAdmin. Admin holds manage_user_membership but none of
        // those, so without this an Admin grants itself one and steps outside the capability model.
        if (PlatformRoles.CarriesPlatformCapability(role) && !await PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            await Send.ResponseAsync(new Response { Message = PlatformRoles.PlatformRoleRefusedMessage }, 403, ct);
            return;
        }

        if (!user.RoleIds.Contains(req.RoleId))
        {
            user.RoleIds.Add(req.RoleId);
            session.Store(user);
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(session, tenant.Slug, "user.role.assigned", actorId, User.FindFirst("Username")?.Value,
                targetType: "User", targetId: req.UserId.ToString(),
                metadata: new() { ["roleId"] = req.RoleId.ToString(), ["roleName"] = role.Name }, ct: ct);
            await session.SaveChangesAsync(ct);

            // This user's effective permissions changed — evict their cached decisions.
            permissionResolver.InvalidateUserPermissions(req.UserId);
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
