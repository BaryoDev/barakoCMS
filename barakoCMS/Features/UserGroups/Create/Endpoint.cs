using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Create;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
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

        var userGroup = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description,
            UserIds = req.UserIds,
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
