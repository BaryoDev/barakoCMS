using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Content.Schedule;

/// <summary>
/// PUT /api/contents/{Id}/schedule — arm (or clear) the times at which the scheduler will publish a
/// Draft or unpublish (Archive) a Published item. The times are stored on the content read model as
/// intent; <see cref="Infrastructure.Services.ScheduledContentService"/> consumes them on its sweep and
/// emits a real ContentStatusChanged event for each transition. Requires the same "update" permission as
/// a status change.
/// </summary>
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public Endpoint(
        IDocumentSession session,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Put("/api/contents/{id}/schedule");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            ThrowError("Invalid or missing User ID claim");
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            ThrowError("Invalid or missing User ID claim");
        }

        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var events = new List<object>
        {
            new barakoCMS.Events.ContentScheduled(content.Id, req.ScheduledPublishAt, req.ScheduledUnpublishAt, userId),
        };

        // Scheduled is a status now, so arming or clearing a publish time is a status change and is
        // recorded as one. Deriving it inside Apply(ContentScheduled) would have been shorter and
        // would have broken the rule this project keeps: a status that moved without a
        // ContentStatusChanged behind it is invisible in the history endpoint and to every workflow
        // watching for a transition, and a replay would produce it from nothing.
        var next = NextStatus(content.Status, req.ScheduledPublishAt);
        if (next is { } status)
        {
            events.Add(new barakoCMS.Events.ContentStatusChanged(content.Id, status, userId));
        }

        await _contentWriter.AppendOptimisticAsync(content, events, ct);
        await _session.SaveChangesAsync(ct);

        await Send.ResponseAsync(new Response
        {
            Message = "Schedule updated",
            ScheduledPublishAt = content.ScheduledPublishAt,
            ScheduledUnpublishAt = content.ScheduledUnpublishAt,
            Status = content.Status,
        });
    }

    /// <summary>The status this schedule moves the entry to, or null when it does not move.</summary>
    /// <remarks>
    /// Only the two directions between Draft and Scheduled. A Published entry stays Published
    /// whatever is armed on it, because arming a future unpublish does not un-publish anything, and
    /// an Archived entry is not brought back to life by a date.
    /// </remarks>
    private static barakoCMS.Models.ContentStatus? NextStatus(
        barakoCMS.Models.ContentStatus current, DateTime? scheduledPublishAt)
    {
        if (current == barakoCMS.Models.ContentStatus.Draft && scheduledPublishAt is not null)
        {
            return barakoCMS.Models.ContentStatus.Scheduled;
        }

        if (current == barakoCMS.Models.ContentStatus.Scheduled && scheduledPublishAt is null)
        {
            return barakoCMS.Models.ContentStatus.Draft;
        }

        return null;
    }
}
