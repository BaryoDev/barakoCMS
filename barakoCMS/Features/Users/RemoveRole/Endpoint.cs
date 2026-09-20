using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.RemoveRole;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/users/{userId}/roles/{roleId}");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await session.LoadAsync<User>(req.UserId, ct);

        if (user == null)
        {
            await Send.NotFoundAsync(ct);
            return;
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
