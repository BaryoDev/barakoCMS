using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Get;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(IQuerySession session, barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Get("/api/contents/{id}");
        // Removed AllowAnonymous to force authentication, assuming JWT is sent.
        // If public access involves "Public" role logic, that should be handled by an "Anonymous User" concept.
        // For now, removing AllowAnonymous makes it secure by default for authenticated users.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Authenticate User
        // Note: Generic "User" principal is available via HttpContext if authenticated.
        var userIdClaim = User.FindFirst("System.Security.Claims.ClaimTypes.NameIdentifier") ?? User.FindFirst("UserId");

        Models.User? user = null;
        if (userIdClaim != null && Guid.TryParse(userIdClaim.Value, out var userId))
        {
            // We need full user for roles.
            // Using IQuerySession to load user is fine.
            user = await _session.LoadAsync<Models.User>(userId, ct);
        }
        else
        {
            // Anonymous Access Handling
            // If we want to support public read, we need a separate mechanism or a "Guest" user.
            // Current strict requirement: Enforce Permissions.
            // If no user -> 401 Unauthorized
            await SendUnauthorizedAsync(ct);
            return;
        }

        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        // 2. Authorize Read
        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            // 403 Forbidden
            await SendForbiddenAsync(ct);
            return;
        }

        Response = new Response
        {
            Id = content.Id,
            ContentType = content.ContentType,
            Data = new Dictionary<string, object>(content.Data),
            CreatedAt = content.CreatedAt,
            UpdatedAt = content.UpdatedAt,
            Status = content.Status,
            LastModifiedBy = content.LastModifiedBy,
            Sensitivity = content.Sensitivity
        };

        var sensitivityService = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        sensitivityService.Apply(Response, HttpContext);
    }
}
