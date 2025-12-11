using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Get;

public class Endpoint : Endpoint<Request, Role>
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
            await SendNotFoundAsync(ct);
            return;
        }

        await SendOkAsync(role, ct);
    }
}
