using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignRole;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/users/{userId}/roles");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Load or create user (for testing, we'll create if not exists)
        var user = await _session.LoadAsync<User>(req.UserId, ct);
        if (user == null)
        {
            user = new User { Id = req.UserId, RoleIds = new() };
        }

        if (!user.RoleIds.Contains(req.RoleId))
        {
            user.RoleIds.Add(req.RoleId);
            _session.Store(user);
            await _session.SaveChangesAsync(ct);
        }

        await SendOkAsync(new Response { Message = "Role assigned to user successfully" }, ct);
    }
}

public class Request
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
