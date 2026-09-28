using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.RemoveRole;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    private static readonly string[] LegacyRoles = ["SuperAdmin", "Admin"];

    public override void Configure()
    {
        Delete("/api/users/{userId}/roles/{roleId}");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!await PlatformRoles.MayChangeAsync(session, User, configuration, LegacyRoles, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Taking SuperAdmin away is a SuperAdmin act, the same as granting it.
        if (req.RoleId == SystemRoles.SuperAdminRoleId && !await PlatformRoles.IsSuperAdminAsync(session, User, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var user = await session.LoadAsync<User>(req.UserId, ct);

        if (user == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.RoleId == SystemRoles.SuperAdminRoleId && user.RoleIds.Contains(req.RoleId))
        {
            // Serialised so two SuperAdmins removing each other at once cannot both see the other
            // as the one who remains. The lock is held until SaveChangesAsync commits.
            await session.BeginTransactionAsync(ct);
            await session.QueryAsync<int>(
                "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, "barakocms:superadmin-holders");

            var others = await session.Query<User>()
                .AnyAsync(u => u.Id != user.Id && u.RoleIds.Contains(SystemRoles.SuperAdminRoleId), ct);
            if (!others)
            {
                await Send.ResponseAsync(new Response
                {
                    Message = "Cannot remove the last SuperAdmin. Grant SuperAdmin to another user first."
                }, 409, ct);
                return;
            }
        }

        user.RoleIds.Remove(req.RoleId);
        session.Store(user);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "user.role.removed", actorId, User.FindFirst("Username")?.Value,
            targetType: "User", targetId: req.UserId.ToString(), metadata: new() { ["roleId"] = req.RoleId.ToString() }, ct: ct);
        await session.SaveChangesAsync(ct);

        // Removing a role narrows the user's access — evict cached decisions so it applies now.
        permissionResolver.InvalidateUserPermissions(req.UserId);

        await Send.OkAsync(new Response { Message = "Role removed from user successfully" }, ct);
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
