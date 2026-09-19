using Marten;
using barakoCMS.Core.Interfaces;

namespace barakoCMS.Features.Content.Get;

/// <summary>
/// The authoring read of one entry, once the caller is known to be allowed to read it.
/// </summary>
/// <remarks>
/// Shared by <c>GET /api/contents/{id}</c> and <c>GET /api/contents/by-slug/{type}/{slug}</c>. The
/// two differ only in how they find the entry, so the ETag, the stream version and the sensitivity
/// scrub live here once. A second copy is exactly how a slug read would start leaking a field the id
/// read masks.
/// </remarks>
internal static class EntryResponse
{
    public static async Task<Response> BuildAsync(
        barakoCMS.Models.Content content,
        IQuerySession session,
        IContentSourcingPolicy sourcing,
        ISensitivityService sensitivity,
        HttpContext http,
        CancellationToken ct)
    {
        var streamState = await session.Events.FetchStreamStateAsync(content.Id, ct);

        // #565 / D16: the document's own Marten version, exposed as a standard ETag so a client can
        // do read-modify-write safely. Gated on the same eventSourced check Update/Endpoint.cs uses
        // for If-Match, and for the same reason: an event-sourced type's PUT does not consult
        // If-Match at all (it already has its own expected-version check on the stream, D3), so an
        // ETag here would promise a precondition nothing on the write side honours. A client that
        // did a correct read-modify-write against one would believe it was protected when nothing
        // was checking. One header, one meaning: emit it only where PUT will act on it.
        if (!await sourcing.IsEventSourcedAsync(content.ContentType, ct))
        {
            var metadata = await session.MetadataForAsync(content, ct);
            if (metadata is not null)
            {
                http.Response.Headers.ETag = ContentETag.Format(metadata.CurrentVersion);
            }
        }

        var response = new Response
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
            ScheduledSensitivity = content.ScheduledSensitivity,
            ScheduledSensitivityAt = content.ScheduledSensitivityAt is { } s
                ? new DateTimeOffset(DateTime.SpecifyKind(s, DateTimeKind.Utc))
                : null,
            Version = streamState?.Version ?? 0
        };

        if (await sensitivity.ApplyAsync(response.ContentType, response.Sensitivity, response.Data, http, ct))
            response.ContentType = "HIDDEN";

        return response;
    }
}
