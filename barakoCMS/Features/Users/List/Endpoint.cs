using FastEndpoints;
using barakoCMS.Infrastructure.Auth;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.List;

internal class Request : PaginatedRequest { }

internal class UserResponse
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public List<Guid> RoleIds { get; set; } = new();
    public List<Guid> GroupIds { get; set; } = new();
    public DateTime CreatedAt { get; set; }

    /// <summary>True when a SuperAdmin has blocked the account. Absent when it is not blocked.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? Blocked { get; set; }
}

internal class Endpoint(IDocumentSession session) : Endpoint<Request, PaginatedResponse<UserResponse>>
{
    public override void Configure()
    {
        Get("/api/users");
        Definition.RequireCapability(SystemCapabilities.ManageUsers, "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var page = await session.Query<User>()
            .OrderByDescending(u => u.CreatedAt)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(page.Map(u => new UserResponse
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            RoleIds = u.RoleIds,
            GroupIds = u.GroupIds,
            CreatedAt = u.CreatedAt,
            Blocked = u.BlockedAt is null ? null : true,
        }), cancellation: ct);
    }
}
