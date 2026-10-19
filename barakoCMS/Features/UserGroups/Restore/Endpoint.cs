using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Audit;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Restore;

/// <summary>POST /api/user-groups/{id}/restore: brings a deleted group back.</summary>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
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

        group.DeletedAt = null;
        session.Store(group);
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
        await AuditLog.RecordAsync(session, tenant.Slug, "usergroup.restored", actorId, User.FindFirst("Username")?.Value,
            targetType: "UserGroup", targetId: group.Id.ToString(), metadata: new() { ["name"] = group.Name }, ct: ct);
        await session.SaveChangesAsync(ct);

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
