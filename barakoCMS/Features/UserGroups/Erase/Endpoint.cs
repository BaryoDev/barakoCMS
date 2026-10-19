using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Erase;

/// <summary>
/// DELETE /api/user-groups/{id}/erase: removes a deleted group for good. It only takes a group that
/// is already deleted, so a live one cannot be destroyed in one call.
/// </summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/user-groups/{id}/erase");
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

        session.Delete(group);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "usergroup.erased", actorId, User.FindFirst("Username")?.Value,
            targetType: "UserGroup", targetId: group.Id.ToString(), metadata: new() { ["name"] = group.Name }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Message = "User group erased" }, ct);
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
