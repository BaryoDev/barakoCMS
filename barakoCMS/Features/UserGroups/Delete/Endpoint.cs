using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Delete;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/user-groups/{id}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.Id, ct);

        if (group == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        // Check referential integrity - ensure no users belong to this group
        var usersInGroup = await _session.Query<User>()
            .AnyAsync(u => u.GroupIds.Contains(req.Id), ct);

        if (usersInGroup)
        {
            await SendAsync(new Response
            {
                Message = "Cannot delete user group: it still has members. Remove all users from the group first."
            }, 409, ct);
            return;
        }

        _session.Delete(group);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response { Message = "User group deleted successfully" }, ct);
    }
}

public class Request
{
    public Guid Id { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
