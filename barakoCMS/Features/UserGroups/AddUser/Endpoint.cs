using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Features.Users;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.AddUser;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/user-groups/{groupId}/users");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await session.LoadAsync<UserGroup>(req.GroupId, ct);

        if (group == null || group.DeletedAt is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user is null || !await PlatformRoles.InTenantAsync(session, tenant, user, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (group.RoleIds.Count > 0
            && await GroupRoleGuard.CheckAsync(session, User, configuration, [], group.RoleIds, ct) is { } refused)
        {
            await Send.ResponseAsync(new Response { Message = refused.Message }, refused.Status, ct);
            return;
        }

        if (!group.UserIds.Contains(req.UserId))
        {
            group.UserIds.Add(req.UserId);
            session.Store(group);
            await session.SaveChangesAsync(ct);
            permissionResolver.InvalidateUserPermissions(req.UserId);
        }

        await Send.OkAsync(new Response { Message = "User added to group successfully" }, ct);
    }
}

internal class Request
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
