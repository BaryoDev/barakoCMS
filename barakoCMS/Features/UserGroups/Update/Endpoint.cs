using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Update;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    IConfiguration configuration) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/user-groups/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await session.LoadAsync<UserGroup>(req.Id, ct);

        if (group == null || group.DeletedAt is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Null leaves the roles as they are, so a caller written before groups held roles keeps working.
        var rolesChanged = false;
        if (req.RoleIds is not null)
        {
            var roleIds = req.RoleIds.Distinct().ToList();
            if (await GroupRoleGuard.CheckAsync(session, User, configuration, group.RoleIds, roleIds, ct) is { } refused)
            {
                await Send.ResponseAsync(new Response { Message = refused.Message }, refused.Status, ct);
                return;
            }

            rolesChanged = !roleIds.Order().SequenceEqual(group.RoleIds.Order());
            group.RoleIds = roleIds;
        }

        group.Name = req.Name;
        group.Description = req.Description;

        session.Store(group);
        await session.SaveChangesAsync(ct);
        if (rolesChanged) permissionResolver.InvalidateAllPermissions();

        await Send.OkAsync(new Response { Message = "User group updated successfully" }, ct);
    }
}

internal class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Guid>? RoleIds { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
