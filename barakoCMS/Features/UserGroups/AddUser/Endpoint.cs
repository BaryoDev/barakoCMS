using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.UserGroups.AddUser;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/user-groups/{groupId}/users");
        Definition.RequireCapability(SystemCapabilities.ManageUserGroups, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var group = await _session.LoadAsync<UserGroup>(req.GroupId, ct);

        if (group == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!group.UserIds.Contains(req.UserId))
        {
            group.UserIds.Add(req.UserId);
            _session.Store(group);
            await _session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Message = "User added to group successfully" }, ct);
    }
}

internal class Request
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}
