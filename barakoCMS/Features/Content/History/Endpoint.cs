using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.History;

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
        Get("/api/contents/{id}/history");
        // Authenticated + per-content "read" permission, matching Content/Get.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Authenticate
        var userIdClaim = User.FindFirst("System.Security.Claims.ClaimTypes.NameIdentifier") ?? User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<Models.User>(userId, ct);
        if (user == null)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        // 2. Load current content and authorize "read" on it (same gate as GET /api/contents/{id}).
        var content = await _session.LoadAsync<Models.Content>(req.Id, ct);
        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        if (!await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        var events = await _session.Events.FetchStreamAsync(req.Id, token: ct);

        var versions = events.Select(e =>
        {
            if (e.Data is barakoCMS.Events.ContentCreated created)
            {
                return new VersionResponse
                {
                    Id = created.Id,
                    Data = created.Data,
                    UpdatedAt = e.Timestamp.DateTime,
                    LastModifiedBy = created.CreatedBy,
                    VersionId = e.Id,
                    Timestamp = e.Timestamp
                };
            }
            else if (e.Data is barakoCMS.Events.ContentUpdated updated)
            {
                return new VersionResponse
                {
                    Id = updated.Id,
                    Data = updated.Data,
                    UpdatedAt = e.Timestamp.DateTime,
                    LastModifiedBy = updated.UpdatedBy,
                    VersionId = e.Id,
                    Timestamp = e.Timestamp
                };
            }
            return null;
        })
        .Where(v => v != null)
        .Cast<VersionResponse>()
        .ToList();

        // 3. Apply the same document- and field-level sensitivity as Get/List to every historical
        // version, based on the current content's sensitivity level and schema.
        var sensitivity = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        foreach (var version in versions)
        {
            version.Data ??= new Dictionary<string, object>();
            sensitivity.Apply(content.ContentType, content.Sensitivity, version.Data, HttpContext);
        }

        await SendAsync(new Response
        {
            Versions = versions
        });
    }
}
