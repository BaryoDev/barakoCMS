using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.Update;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Put("/api/user-groups/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.Id, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        group.Name = req.Name;
        group.Description = req.Description;

        _session.Store(group);
        await _session.SaveChangesAsync(ct);

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
