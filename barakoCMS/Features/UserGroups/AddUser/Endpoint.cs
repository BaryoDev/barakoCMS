using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.AddUser;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/user-groups/{groupId}/users");
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

        if (!group.UserIds.Contains(req.UserId))
        {
            group.UserIds.Add(req.UserId);
            _session.Store(group);
            await _session.SaveChangesAsync(ct);
        }

        await SendOkAsync(new Response { Message = "User added to group successfully" }, ct);
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
