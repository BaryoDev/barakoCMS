using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Create;

internal class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/user-groups");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
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

        session.Store(userGroup);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response
        {
            Id = userGroup.Id,
            Message = "User group created successfully"
        }, ct);
    }
}
