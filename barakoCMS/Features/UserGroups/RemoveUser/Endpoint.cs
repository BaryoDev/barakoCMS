using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.RemoveUser;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/user-groups/{groupId}/users/{userId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.GroupId, ct);

        if (group == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        group.UserIds.Remove(req.UserId);
        _session.Store(group);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response { Message = "User removed from group successfully" }, ct);
    }
}

public class Request
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
