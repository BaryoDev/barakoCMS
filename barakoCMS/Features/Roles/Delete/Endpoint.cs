using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Delete;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Delete("/api/roles/{id}");
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

        _session.Delete(role);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Message = "Role deleted successfully"
        }, ct);
    }
}
