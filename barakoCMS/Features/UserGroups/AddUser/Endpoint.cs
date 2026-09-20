using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.AddUser;

internal class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/user-groups/{groupId}/users");
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

        if (!group.UserIds.Contains(req.UserId))
        {
            group.UserIds.Add(req.UserId);
            session.Store(group);
            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Message = "User added to group successfully" }, ct);
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
