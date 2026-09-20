using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.RemoveUser;

internal class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Delete("/api/user-groups/{groupId}/users/{userId}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await session.LoadAsync<UserGroup>(req.GroupId, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        group.UserIds.Remove(req.UserId);
        session.Store(group);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Message = "User removed from group successfully" }, ct);
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
