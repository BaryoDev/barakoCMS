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
