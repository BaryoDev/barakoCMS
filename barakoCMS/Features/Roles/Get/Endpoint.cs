using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Get;

internal class Endpoint : Endpoint<Request, barakoCMS.Features.Roles.RoleResponse>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/roles/{id}");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = await _session.LoadAsync<Role>(req.Id, ct);

        if (role == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(barakoCMS.Features.Roles.RoleResponse.From(role), ct);
    }
}
