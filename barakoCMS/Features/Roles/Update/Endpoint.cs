using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Update;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Put("/api/roles/{id}");
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

        // Update role properties
        role.Name = req.Name;
        role.Description = req.Description;
        role.Permissions = req.Permissions;
        role.SystemCapabilities = req.SystemCapabilities;

        _session.Store(role);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Message = "Role updated successfully"
        }, ct);
    }
}
