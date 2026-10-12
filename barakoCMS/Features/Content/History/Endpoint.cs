using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.History;

internal class Endpoint(
    IQuerySession session,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver) : Endpoint<Request, barakoCMS.Models.PaginatedResponse<VersionResponse>>
{
    public override void Configure()
    {
        Get("/api/contents/{id}/history");
        // Authenticated + per-content "read" permission, matching Content/Get.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Authenticate
        // "UserId" is the only identity claim the token carries. This used to look first for the
        // literal string System.Security.Claims.ClaimTypes.NameIdentifier, which is the name of a
        // constant and not its value, so it never matched and the fallback was always what ran.
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var user = await session.LoadAsync<Models.User>(userId, ct);
        if (user == null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // 2. Load current content and authorize "read" on it (same gate as GET /api/contents/{id}).
        var content = await session.LoadAsync<Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var events = await session.Events.FetchStreamAsync(req.Id, token: ct);

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
                case barakoCMS.Events.ContentSensitivityScheduled sensitivityScheduled:
                    version.LastModifiedBy = sensitivityScheduled.UpdatedBy;
                    version.ScheduledSensitivity = sensitivityScheduled.ScheduledSensitivity;
                    version.ScheduledSensitivityAt = sensitivityScheduled.ScheduledSensitivityAt;
                    break;
                case barakoCMS.Events.ContentFieldSensitivityChanged fieldSensitivityChanged:
                    version.LastModifiedBy = fieldSensitivityChanged.ChangedBy;
                    break;
            }

            return version;
        })
        .ToList();

        // 3. Apply the same document- and field-level sensitivity as Get/List to every historical
        // version that carries a document, based on the current content's sensitivity level and
        // schema. The entries with no data have nothing to mask.
        var sensitivity = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        var stored = new Dictionary<VersionResponse, Dictionary<string, object>>(ReferenceEqualityComparer.Instance);
        foreach (var version in versions.Where(v => v.Data != null))
        {
            stored[version] = new Dictionary<string, object>(version.Data!);
            await sensitivity.ApplyAsync(content, version.Data!, HttpContext, ct);
        }

        // Every version's past values count as a read, recorded once for the page that goes out.
        var page = versions.ToPagedResponse(req);
        var sent = page.Items
            .Where(v => v.Data != null && stored.ContainsKey(v))
            .Select(v => (stored[v], v.Data!))
            .ToList();
        var type = content.ContentType;
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        await barakoCMS.Infrastructure.Audit.SensitiveReadAudit.RecordAsync(
            HttpContext,
            [await barakoCMS.Infrastructure.Audit.SensitiveReadAudit.DescribeVersionsAsync(
                content, definition, sent, sensitivity, HttpContext, ct)],
            ct);

        await Send.ResponseAsync(page);
    }

    /// <summary>The wire name for an event, decided here rather than reflected from the CLR type.</summary>
    /// <remarks>
    /// <c>e.Data.GetType().Name</c> is the obvious way to write this, and it quietly makes every
    /// event record's class name part of the API: renaming <c>ContentStatusChanged</c> would change
    /// what clients receive, with nothing to warn anyone. This mapper exists so the event shapes stay
    /// free to change, so the discriminator has to be a decision rather than a reflection of the
    /// type it happens to be built from.
    ///
    /// An unrecognised event still produces an entry, because appearing beats disappearing, and the
    /// count of entries has to keep matching the count of events in the stream.
    ///
    /// It does NOT fall back to the CLR type name. That was the first version and it defeated the
    /// point: adding an event and forgetting this switch would have put its class name on the wire,
    /// which is the leak #229 forbids, and no reflection guard can see it because by then it is a
    /// string. "Unknown" says the same thing to a client, and the omission is still visible, in the
    /// place that can act on it rather than in a response.
    /// </remarks>
    private static string ChangeTypeOf(object @event) => @event switch
    {
        barakoCMS.Events.ContentCreated => "Created",
        barakoCMS.Events.ContentUpdated => "Updated",
        barakoCMS.Events.ContentStatusChanged => "StatusChanged",
        barakoCMS.Events.ContentScheduled => "Scheduled",
        barakoCMS.Events.ContentSensitivityChanged => "SensitivityChanged",
        barakoCMS.Events.ContentSensitivityScheduled => "SensitivityScheduled",
        barakoCMS.Events.ContentTransitioned => "Transitioned",
        barakoCMS.Events.ContentFieldSensitivityChanged => "FieldSensitivityChanged",
        _ => UnknownChangeType,
    };

    /// <summary>What an event this mapper does not know is reported as.</summary>
    /// <remarks>
    /// Public so a test can pin it. It is wire contract like the other five, and the value being
    /// deliberately uninformative is the property worth asserting.
    /// </remarks>
    internal const string UnknownChangeType = "Unknown";
}
