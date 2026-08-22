using barakoCMS.Core.Interfaces;
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
    private readonly IContentWriter _contentWriter;

    public RollbackEndpoint(IDocumentSession session, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
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

        // 4. Load the current content
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            AddError("Content not found");
            await SendErrorsAsync(cancellation: ct);
            return;
        }

        // Rebuild SearchText using the current field-sensitivity definition so a rollback
        // cannot reintroduce values that are no longer Public into the searchable text.
        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchText = string.Join(
            ' ',
            data
                .Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));

        // 5. Create a new update event with the old data and rebuilt SearchText
        var rollbackEvent = new ContentUpdated(req.Id, data, userId, searchText);

        // 6. Append the new event and update the document together
        _contentWriter.Append(content, rollbackEvent);

        await _session.SaveChangesAsync(ct);

        // 8. Return the new state
        await SendAsync(content, cancellation: ct);
    }
}
