using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.List;

public class Endpoint : Endpoint<ListRequest, PaginatedResponse<UserGroup>>
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

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<UserGroup>()
            .OrderBy(g => g.Name)
            .ToPagedResponseAsync(req, ct);

        await Send.OkAsync(page, ct);
    }
}
