using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignGroup;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/users/{userId}/groups");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Load or create user
        var user = await _session.LoadAsync<User>(req.UserId, ct);
        if (user == null)
        {
            user = new User 
            { 
                Id = req.UserId, 
                GroupIds = new(),
                Username = $"user_{req.UserId:N}",
                Email = $"user_{req.UserId:N}@example.com",
                CreatedAt = DateTime.UtcNow
            };
        }

        if (!user.GroupIds.Contains(req.GroupId))
        {
            user.GroupIds.Add(req.GroupId);
            _session.Store(user);
            await _session.SaveChangesAsync(ct);
        }

        await SendOkAsync(new Response { Message = "User added to group successfully" }, ct);
    }
}

public class Request
{
    public Guid UserId { get; set; }
    public Guid GroupId { get; set; }
}

public class Response
{
    public string Message { get; set; } = string.Empty;
}
