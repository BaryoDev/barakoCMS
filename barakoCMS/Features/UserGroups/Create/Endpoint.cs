using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Features.Users;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Create;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/user-groups");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var roleIds = (req.RoleIds ?? []).Distinct().ToList();
        if (await GroupRoleGuard.CheckAsync(session, User, configuration, [], roleIds, ct) is { } refused)
        {
            await Send.ResponseAsync(new Response { Message = refused.Message }, refused.Status, ct);
            return;
        }

        var userIds = (req.UserIds ?? []).Distinct().ToList();
        foreach (var userId in userIds)
        {
            var user = await session.LoadAsync<User>(userId, ct);
            if (user is null || !await PlatformRoles.InTenantAsync(session, tenant, user, ct))
            {
                await Send.NotFoundAsync(ct);
                return;
            }
        }

        var userGroup = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description,
            UserIds = userIds,
            RoleIds = roleIds
        };

        session.Store(userGroup);
        await session.SaveChangesAsync(ct);
        if (roleIds.Count > 0) permissionResolver.InvalidateAllPermissions();

        await Send.OkAsync(new Response
        {
            Id = userGroup.Id,
            Message = "User group created successfully"
        }, ct);
    }
}
