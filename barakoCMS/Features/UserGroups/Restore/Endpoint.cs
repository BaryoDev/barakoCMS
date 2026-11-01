using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Restore;

/// <summary>POST /api/user-groups/{id}/restore: brings a deleted group back.</summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissions) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/user-groups/{id}/restore");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await session.LoadAsync<UserGroup>(req.Id, ct);
        if (group == null || group.DeletedAt is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Roles/Delete counts only live groups, so a deleted group can still name a role that has
        // since gone. Drop those ids rather than refuse, which would be a new status code.
        var liveRoles = group.RoleIds.Count == 0 ? [] : (await session.Query<Role>()
            .Where(r => group.RoleIds.Contains(r.Id) && r.DeletedAt == null)
            .Select(r => r.Id).ToListAsync(ct)).ToHashSet();
        var dropped = group.RoleIds.Where(id => !liveRoles.Contains(id)).ToList();
        group.RoleIds = group.RoleIds.Where(liveRoles.Contains).ToList();

        group.DeletedAt = null;
        session.Store(group);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "usergroup.restored", actorId, User.FindFirst("Username")?.Value,
            targetType: "UserGroup", targetId: group.Id.ToString(), metadata: dropped.Count == 0
                ? new() { ["name"] = group.Name }
                : new() { ["name"] = group.Name, ["droppedRoleIds"] = string.Join(",", dropped) }, ct: ct);
        await session.SaveChangesAsync(ct);
        if (group.RoleIds is { Count: > 0 } || dropped.Count > 0)
        {
            permissions.InvalidateAllPermissions();
        }

        await Send.OkAsync(new Response { Message = "User group restored successfully" }, ct);
    }
}

internal class Request
{
    public Guid Id { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
