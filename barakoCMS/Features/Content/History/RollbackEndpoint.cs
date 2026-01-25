using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Events;

namespace barakoCMS.Features.Content.History;

public class RollbackRequest
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; } // The ID of the event to rollback to
}

public class RollbackEndpoint : Endpoint<RollbackRequest, barakoCMS.Models.Content>
{
    private readonly IDocumentSession _session;

    public RollbackEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/contents/{id}/rollback/{versionId}");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(RollbackRequest req, CancellationToken ct)
    {
        // Extract userId from claims for audit trail
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            AddError("User ID claim not found");
            await SendErrorsAsync(400, ct);
            return;
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            AddError("Invalid User ID format");
            await SendErrorsAsync(400, ct);
            return;
        }

        // 1. Fetch the event stream
        var events = await _session.Events.FetchStreamAsync(req.Id, token: ct);

        // 2. Find the target event
        var targetEvent = events.FirstOrDefault(e => e.Id == req.VersionId);

        if (targetEvent == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        // 3. Extract data from the event
        Dictionary<string, object> data = new();
        if (targetEvent.Data is ContentCreated created)
        {
            data = created.Data;
        }
        else if (targetEvent.Data is ContentUpdated updated)
        {
            data = updated.Data;
        }
        else
        {
            AddError("Cannot rollback to this version type.");
            await SendErrorsAsync(cancellation: ct);
            return;
        }

        // 4. Create a new update event with the old data
        var rollbackEvent = new ContentUpdated(req.Id, data, userId);

        // 5. Append the new event
        _session.Events.Append(req.Id, rollbackEvent);

        // 6. Apply rollback to the document in same transaction
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content != null)
        {
            content.Apply(rollbackEvent);
            _session.Store(content);
        }

        await _session.SaveChangesAsync(ct);

        // 7. Return the new state
        if (content == null)
        {
            AddError("Content not found after rollback");
            await SendErrorsAsync(cancellation: ct);
            return;
        }

        await SendAsync(content, cancellation: ct);
    }
}
