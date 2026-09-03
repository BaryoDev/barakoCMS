using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using barakoCMS.Events;

namespace barakoCMS.Features.Content.History;

internal class RollbackRequest
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; } // The ID of the event to rollback to
}

internal class RollbackEndpoint : Endpoint<RollbackRequest, RollbackResponse>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Services.IPermissionResolver _permissionResolver;

    public RollbackEndpoint(
        IDocumentSession session,
        IContentWriter contentWriter,
        barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver)
    {
        _contentWriter = contentWriter;
        _session = session;
        _permissionResolver = permissionResolver;
    }

    public override void Configure()
    {
        Post("/api/contents/{id}/rollback/{versionId}");
        // Not the same capability as the erasure next door. That gate was Roles("SuperAdmin") and
        // this one was Roles("SuperAdmin", "Admin"), so one name would have to widen or narrow one
        // of them, and a rollback writes a new version where the erasure destroys the history.
        Definition.RequireCapability(SystemCapabilities.RollbackContent, "SuperAdmin", "Admin");
    }
    
    public override async Task HandleAsync(RollbackRequest req, CancellationToken ct)
    {
        // Extract userId from claims for audit trail
        var userIdClaim = User.FindFirst("UserId");
        if (userIdClaim == null)
        {
            AddError("User ID claim not found");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!Guid.TryParse(userIdClaim.Value, out var userId))
        {
            AddError("Invalid User ID format");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // 1. Load the current content
        //
        // Before the stream, so authorisation can run before anything is read or reported. Loading
        // the stream first let an unauthorised caller tell a real version from an invented one by
        // the status code, and did the work of reading every event for them on the way. A missing
        // content answers 404, which is what an unknown id already produced from the event check
        // below, so nothing observable changes for a caller who is allowed to be here.
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 2. PERMISSION: the role gate on the route says who may reach it at all. This says whether
        // this caller may update this content type, which is the permission a rollback actually
        // exercises, since the write it performs is indistinguishable from PUT /api/contents/{id}.
        // Without it an Admin with no update grant on a type could rewrite an entry of that type by
        // restoring it, while being refused the history that lists what to restore.
        //
        // It runs before the stream is read, so a caller who may not write learns nothing about
        // what versions exist. See #447.
        var actor = await _session.LoadAsync<barakoCMS.Models.User>(userId, ct);
        if (actor == null || !await _permissionResolver.CanPerformActionAsync(
                actor, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // 3. Fetch the event stream
        var events = await _session.Events.FetchStreamAsync(req.Id, token: ct);

        // 4. Find the target event
        var targetEvent = events.FirstOrDefault(e => e.Id == req.VersionId);

        if (targetEvent == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 5. Extract data from the event
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
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // A rollback is an update, so it runs the same four gates an update runs. It used to run
        // none of them, which meant restoring an old version could put back data the current schema
        // rejects, change a field the caller is not allowed to change, or break an invariant that
        // was introduced after the version being restored.
        //
        // The count was three until #447: permission was the one an update runs that this did not,
        // so a role gate that named Admin was the whole of the authorisation on a write path.
        //
        // The awkward part is that the "new" data here is old data rather than something a caller
        // typed, so an operator can be refused a rollback for a reason that predates them. That is
        // the correct answer: the alternative is a write path that launders rejected data back in,
        // and it is reachable by anyone who can press Restore.

        // WRITE-PATH SENSITIVITY: a caller who may not see a field may not change it, and restoring
        // an old value is a change. Reverts any such field to what is stored.
        await Resolve<barakoCMS.Core.Interfaces.ISensitivityService>()
            .ApplyWriteAsync(content.ContentType, data, content.Data, HttpContext, ct);

        var validationResult = await Resolve<barakoCMS.Infrastructure.Services.IContentValidatorService>()
            .ValidateAsync(content.ContentType, data);
        if (!validationResult.IsValid)
        {
            foreach (var error in validationResult.Errors)
            {
                AddError($"This version cannot be restored: {error}");
            }

            await Send.ErrorsAsync(400, ct);
            return;
        }

        var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(content.ContentType, data, content.Data, userId, ct);
        if (hookErrors.Count > 0)
        {
            foreach (var error in hookErrors)
            {
                AddError($"This version cannot be restored: {error}");
            }

            await Send.ErrorsAsync(400, ct);
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
        await _contentWriter.AppendAsync(content, rollbackEvent, ct);

        await _session.SaveChangesAsync(ct);

        // 8. Return the new state
        await Send.ResponseAsync(RollbackResponse.From(content), cancellation: ct);
    }
}
