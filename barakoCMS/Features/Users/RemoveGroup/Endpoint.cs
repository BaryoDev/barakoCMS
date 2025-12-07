using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.RemoveGroup;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/users/{userId}/groups/{groupId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var user = await _session.LoadAsync<User>(req.UserId, ct);

        if (user == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        user.GroupIds.Remove(req.GroupId);
        _session.Store(user);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response { Message = "User removed from group successfully" }, ct);
    }
}

public class Request
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
