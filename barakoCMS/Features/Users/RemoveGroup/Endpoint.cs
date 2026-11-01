using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.RemoveGroup;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/users/{userId}/groups/{groupId}");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await session.LoadAsync<User>(req.UserId, ct);

        if (user == null || !await PlatformRoles.InTenantAsync(session, tenant, user, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var group = await session.LoadAsync<UserGroup>(req.GroupId, ct);
        if (group is { RoleIds.Count: > 0 }
            && await barakoCMS.Features.UserGroups.GroupRoleGuard.CheckAsync(session, User, configuration, group.RoleIds, [], ct) is { } refused)
        {
            await Send.ResponseAsync(new Response { Message = refused.Message }, refused.Status, ct);
            return;
        }

        user.GroupIds.Remove(req.GroupId);
        session.Store(user);

        // Membership is read from either side, so leaving only one would keep the group's roles.
        if (group is not null && group.UserIds.Remove(req.UserId))
            session.Store(group);

        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "user.group.removed", actorId, User.FindFirst("Username")?.Value,
            targetType: "User", targetId: req.UserId.ToString(), metadata: new() { ["groupId"] = req.GroupId.ToString() }, ct: ct);
        await session.SaveChangesAsync(ct);
        permissionResolver.InvalidateUserPermissions(req.UserId);

        await Send.OkAsync(new Response { Message = "User removed from group successfully" }, ct);
    }
}

internal class Request
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
