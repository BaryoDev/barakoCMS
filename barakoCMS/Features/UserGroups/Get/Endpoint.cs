using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Get;

public class Endpoint : Endpoint<Request, UserGroup>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/user-groups/{id}");
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

        await SendOkAsync(group, ct);
    }
}

public class Request
{
    public Guid Id { get; set; }
}
