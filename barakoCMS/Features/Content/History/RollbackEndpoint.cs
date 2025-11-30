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
        // We need to know who is doing the rollback. For now, we'll use a placeholder or get from User
        var userId = Guid.Empty; // TODO: Get from Claims

        var rollbackEvent = new ContentUpdated(req.Id, data, userId);

        // 5. Append the new event
        _session.Events.Append(req.Id, rollbackEvent);
        await _session.SaveChangesAsync(ct);

        // 6. Return the new state
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        await SendAsync(content!, cancellation: ct);
    }
}
