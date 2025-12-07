using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Roles.Create;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/roles");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description,
            Permissions = req.Permissions,
            SystemCapabilities = req.SystemCapabilities,
            CreatedAt = DateTime.UtcNow
        };

        _session.Store(role);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Id = role.Id,
            Message = "Role created successfully"
        }, ct);
    }
}
