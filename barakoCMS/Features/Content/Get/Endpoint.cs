using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Get;

internal class Endpoint : Endpoint<Request, Response>
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
        // Authenticated only. Anonymous reads go through the delivery API (/api/public/{type}/{slug}),
        // which serves published entries and public fields; this is the authoring read.
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // 1. Authenticate User
        // Note: Generic "User" principal is available via HttpContext if authenticated.
        // "UserId" is the only identity claim the token carries. This used to look first for the
        // literal string System.Security.Claims.ClaimTypes.NameIdentifier, which is the name of a
        // constant and not its value, so it never matched and the fallback was always what ran.
        var userIdClaim = User.FindFirst("UserId");

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
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 2. Authorize Read
        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "read", content, ct))
        {
            // 403 Forbidden
            await Send.ForbiddenAsync(ct);
            return;
        }

        var streamState = await _session.Events.FetchStreamStateAsync(req.Id, ct);

        // #565 / D16: the document's own Marten version, exposed as a standard ETag so a client can
        // do read-modify-write safely, regardless of content type or sourcing mode. PUT accepts this
        // back as If-Match. Ships unconditionally; Content:Concurrency:Require only governs what
        // happens on the PUT side to a caller that sends neither.
        var metadata = await _session.MetadataForAsync(content, ct);
        if (metadata is not null)
        {
            HttpContext.Response.Headers.ETag = ContentETag.Format(metadata.CurrentVersion);
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
            Sensitivity = content.Sensitivity,
            // Stored as DateTime with Kind Utc. Stated explicitly rather than relying on the
            // implicit conversion, which would read an Unspecified Kind as local time.
            ScheduledPublishAt = content.ScheduledPublishAt is { } p
                ? new DateTimeOffset(DateTime.SpecifyKind(p, DateTimeKind.Utc))
                : null,
            ScheduledUnpublishAt = content.ScheduledUnpublishAt is { } u
                ? new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc))
                : null,
            Version = streamState?.Version ?? 0
        };

        var sensitivityService = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        if (await sensitivityService.ApplyAsync(Response.ContentType, Response.Sensitivity, Response.Data, HttpContext, ct))
            Response.ContentType = "HIDDEN";
    }
}
