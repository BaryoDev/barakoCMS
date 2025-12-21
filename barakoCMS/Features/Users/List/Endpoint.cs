using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.List;

public class Request : PaginatedRequest { }

public class UserResponse
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<Guid> RoleIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class Endpoint : Endpoint<Request, PaginatedResponse<UserResponse>>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/users");
        Roles("SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var query = _session.Query<User>();

        var totalCount = await query.CountAsync(ct);

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);
        
        var response = users.Select(u => new UserResponse
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            RoleIds = u.RoleIds,
            GroupIds = u.GroupIds,
            CreatedAt = u.CreatedAt
        }).ToList();

        await SendAsync(new PaginatedResponse<UserResponse>
        {
            Items = response,
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = totalCount
        }, cancellation: ct);
    }
}
