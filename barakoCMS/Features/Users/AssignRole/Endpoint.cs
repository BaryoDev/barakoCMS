using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;

namespace barakoCMS.Features.Users.AssignRole;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _permissionResolver = permissionResolver;
        _tenant = tenant;
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
            user = new User
            {
                Id = req.UserId,
                RoleIds = new(),
                Username = $"user_{req.UserId:N}",
                Email = $"user_{req.UserId:N}@example.com",
                CreatedAt = DateTime.UtcNow
            };
        }

        if (!user.RoleIds.Contains(req.RoleId))
        {
            user.RoleIds.Add(req.RoleId);
            _session.Store(user);
            Guid.TryParse(User.FindFirst("UserId")?.Value, out var actorId);
            await AuditLog.RecordAsync(_session, _tenant.Slug, "user.role.assigned", actorId, User.FindFirst("Username")?.Value,
                targetType: "User", targetId: req.UserId.ToString(), metadata: new() { ["roleId"] = req.RoleId.ToString() }, ct: ct);
            await _session.SaveChangesAsync(ct);

            // This user's effective permissions changed — evict their cached decisions.
            _permissionResolver.InvalidateUserPermissions(req.UserId);
        }

        await Send.OkAsync(new Response { Message = "Role assigned to user successfully" }, ct);
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
