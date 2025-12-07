using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.List;

public class Endpoint : EndpointWithoutRequest<List<Role>>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/roles");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var roles = await _session.Query<Role>().ToListAsync(ct);
        await SendOkAsync(roles.ToList(), ct);
    }
}
