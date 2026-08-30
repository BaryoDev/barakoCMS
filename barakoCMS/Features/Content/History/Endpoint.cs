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
                ChangeType = ChangeTypeOf(e.Data),
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

    /// <summary>The wire name for an event, decided here rather than reflected from the CLR type.</summary>
    /// <remarks>
    /// <c>e.Data.GetType().Name</c> is the obvious way to write this, and it quietly makes every
    /// event record's class name part of the API: renaming <c>ContentStatusChanged</c> would change
    /// what clients receive, with nothing to warn anyone. This mapper exists so the event shapes stay
    /// free to change, so the discriminator has to be a decision rather than a reflection of the
    /// type it happens to be built from.
    ///
    /// An unrecognised event still falls back to its type name, because appearing under an ugly
    /// label beats disappearing, and it makes the omission visible the first time somebody adds an
    /// event and forgets this switch.
    /// </remarks>
    private static string ChangeTypeOf(object @event) => @event switch
    {
        barakoCMS.Events.ContentCreated => "Created",
        barakoCMS.Events.ContentUpdated => "Updated",
        barakoCMS.Events.ContentStatusChanged => "StatusChanged",
        barakoCMS.Events.ContentScheduled => "Scheduled",
        barakoCMS.Events.ContentSensitivityChanged => "SensitivityChanged",
        _ => @event.GetType().Name,
    };
}
