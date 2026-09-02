using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.RemoveUser;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/user-groups/{groupId}/users/{userId}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.GroupId, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        group.UserIds.Remove(req.UserId);
        _session.Store(group);
        await _session.SaveChangesAsync(ct);

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
