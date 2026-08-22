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
public class Endpoint : Endpoint<Request, Response>
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
        Put("/api/contents/{Id}/schedule");
        Claims("UserId");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await SendAsync(new Response { Message = "Invalid or missing User ID claim" }, 400, ct);
            return;
        }

        var user = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        if (user == null || !await _permissionResolver.CanPerformActionAsync(user, content.ContentType, "update", content, ct))
        {
            await SendForbiddenAsync(ct);
            return;
        }

        _contentWriter.Append(
            content,
            new barakoCMS.Events.ContentScheduled(content.Id, req.ScheduledPublishAt, req.ScheduledUnpublishAt, userId));
        await _session.SaveChangesAsync(ct);

        await SendAsync(new Response
        {
            Message = "Schedule updated",
            ScheduledPublishAt = content.ScheduledPublishAt,
            ScheduledUnpublishAt = content.ScheduledUnpublishAt,
        });
    }
}
