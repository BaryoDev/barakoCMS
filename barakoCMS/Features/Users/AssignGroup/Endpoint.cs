using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignGroup;

internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/users/{userId}/groups");
        Definition.RequireCapability(SystemCapabilities.ManageUserMembership, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Same fabricated-user path as AssignRole next door, and the same fix: an unknown user or an
        // unknown group is a 404, not a success that writes a ghost identity.
        var user = await session.LoadAsync<User>(req.UserId, ct);
        if (user == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var group = await session.LoadAsync<UserGroup>(req.GroupId, ct);
        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!user.GroupIds.Contains(req.GroupId))
        {
            user.GroupIds.Add(req.GroupId);
            session.Store(user);
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(session, tenant.Slug, "user.group.assigned", actorId, User.FindFirst("Username")?.Value,
                targetType: "User", targetId: req.UserId.ToString(), metadata: new() { ["groupId"] = req.GroupId.ToString() }, ct: ct);
            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Message = "User added to group successfully" }, ct);
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
