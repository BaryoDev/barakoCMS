using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Get;

internal class Endpoint : Endpoint<Request, barakoCMS.Features.UserGroups.UserGroupResponse>
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
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(barakoCMS.Features.UserGroups.UserGroupResponse.From(group), ct);
    }
}

internal class Request
{
    public Guid Id { get; set; }
}
