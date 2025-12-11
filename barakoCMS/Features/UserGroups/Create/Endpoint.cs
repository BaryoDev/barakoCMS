using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Create;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/user-groups");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userGroup = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            Description = req.Description,
            UserIds = req.UserIds
        };

        _session.Store(userGroup);
        await _session.SaveChangesAsync(ct);

        await SendOkAsync(new Response
        {
            Id = userGroup.Id,
            Message = "User group created successfully"
        }, ct);
    }
}
