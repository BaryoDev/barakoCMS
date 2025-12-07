using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.List;

public class Endpoint : EndpointWithoutRequest<List<UserGroup>>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/user-groups");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var groups = await _session.Query<UserGroup>().ToListAsync(ct);
        await SendOkAsync(groups.ToList(), ct);
    }
}
