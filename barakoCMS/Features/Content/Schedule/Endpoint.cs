using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Content.Schedule;

/// <summary>
/// PUT /api/contents/{Id}/schedule. Arms (or clears) the times at which the scheduler will publish a
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

        var scheduled = new barakoCMS.Events.ContentScheduled(
            content.Id, req.ScheduledPublishAt, req.ScheduledUnpublishAt, userId);

        try
        {
            // The version-aware overload, not the single-event one. That one appends without ever
            // asking where the stream was, so an event-sourced type documented as answering 409 to a
            // stale write answered 200 here and armed a schedule against a copy that had moved on.
            await _contentWriter.AppendAsync(content, new object[] { scheduled }, req.Version == 0 ? null : req.Version, ct);
            await _session.SaveChangesAsync(ct);
        }
        catch (StaleContentException ex)
        {
            ThrowError(e => e.Version, ex.Message, 409);
        }

        await Send.ResponseAsync(new Response
        {
            Message = "Schedule updated",
            ScheduledPublishAt = content.ScheduledPublishAt,
            ScheduledUnpublishAt = content.ScheduledUnpublishAt,
        });
    }
}
