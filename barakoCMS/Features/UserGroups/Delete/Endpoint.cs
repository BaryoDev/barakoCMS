using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Delete;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Delete("/api/user-groups/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.Id, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Check referential integrity - ensure no users belong to this group
        var usersInGroup = await _session.Query<User>()
            .AnyAsync(u => u.GroupIds.Contains(req.Id), ct);

        if (usersInGroup)
        {
            await Send.ResponseAsync(new Response
            {
                Message = "Cannot delete user group: it still has members. Remove all users from the group first."
            }, 409, ct);
            return;
        }

        _session.Delete(group);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(_session, _tenant.Slug, "usergroup.deleted", actorId, User.FindFirst("Username")?.Value,
            targetType: "UserGroup", targetId: group.Id.ToString(), metadata: new() { ["name"] = group.Name }, ct: ct);
        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Message = "User group deleted successfully" }, ct);
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
