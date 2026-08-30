using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.History;

internal class Endpoint : Endpoint<Request, barakoCMS.Models.PaginatedResponse<VersionResponse>>
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
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await _session.LoadAsync<Models.User>(userId, ct);
        if (user == null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // 2. Load current content and authorize "read" on it (same gate as GET /api/contents/{id}).
        var content = await _session.LoadAsync<Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var events = await _session.Events.FetchStreamAsync(req.Id, token: ct);

        // Every event becomes an entry, including one this mapper does not recognise: it keeps its
        // type name and carries no data, rather than being dropped where nobody would see it go.
        var versions = events.Select(e =>
        {
            var version = new VersionResponse
            {
                Id = req.Id,
                ChangeType = e.Data.GetType().Name,
                VersionId = e.Id,
                Timestamp = e.Timestamp.ToUniversalTime()
            };

            switch (e.Data)
            {
                case barakoCMS.Events.ContentCreated created:
                    version.Data = created.Data;
                    version.LastModifiedBy = created.CreatedBy;
                    version.Status = created.Status;
                    version.Sensitivity = created.Sensitivity;
                    break;
                case barakoCMS.Events.ContentUpdated updated:
                    version.Data = updated.Data;
                    version.LastModifiedBy = updated.UpdatedBy;
                    break;
                case barakoCMS.Events.ContentStatusChanged statusChanged:
                    version.LastModifiedBy = statusChanged.UpdatedBy;
                    version.Status = statusChanged.NewStatus;
                    break;
                case barakoCMS.Events.ContentScheduled scheduled:
                    version.LastModifiedBy = scheduled.UpdatedBy;
                    version.ScheduledPublishAt = scheduled.ScheduledPublishAt;
                    version.ScheduledUnpublishAt = scheduled.ScheduledUnpublishAt;
                    break;
                case barakoCMS.Events.ContentSensitivityChanged sensitivityChanged:
                    version.LastModifiedBy = sensitivityChanged.UpdatedBy;
                    version.Sensitivity = sensitivityChanged.Sensitivity;
                    break;
            }

            return version;
        })
        .ToList();

        // 3. Apply the same document- and field-level sensitivity as Get/List to every historical
        // version that carries a document, based on the current content's sensitivity level and
        // schema. The entries with no data have nothing to mask.
        var sensitivity = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        foreach (var version in versions.Where(v => v.Data != null))
        {
            await sensitivity.ApplyAsync(content.ContentType, content.Sensitivity, version.Data!, HttpContext, ct);
        }

        await Send.ResponseAsync(versions.ToPagedResponse(req));
    }
}
