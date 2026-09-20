using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Update;

internal class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/user-groups/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await session.LoadAsync<UserGroup>(req.Id, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        group.Name = req.Name;
        group.Description = req.Description;

        session.Store(group);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Message = "User group updated successfully" }, ct);
    }
}

internal class Request
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
