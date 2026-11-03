using barakoCMS.Core.Interfaces;
using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Events;

namespace barakoCMS.Features.Content.History;

internal class RollbackRequest
{
    public Guid Id { get; set; }
    public Guid VersionId { get; set; } // The ID of the event to rollback to
}

internal class RollbackEndpoint(
    IDocumentSession session,
    IContentWriter contentWriter,
    barakoCMS.Infrastructure.Services.IPermissionResolver permissionResolver) : Endpoint<RollbackRequest, RollbackResponse>
{
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
        var callerId = await ReadUserIdAsync(ct);
        if (callerId is not { } userId)
        {
            return;
        }

        // 1. Load the current content
        //
        // Before the stream, so authorisation can run before anything is read or reported. Loading
        // the stream first let an unauthorised caller tell a real version from an invented one by
        // the status code, and did the work of reading every event for them on the way. A missing
        // content answers 404, which is what an unknown id already produced from the event check
        // below, so nothing observable changes for a caller who is allowed to be here.
        var content = await session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);
        if (content == null || !await Resolve<barakoCMS.Infrastructure.Services.IBranchScopeGuard>().MayAccessAsync(User, content, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var loadedVersion = (await session.MetadataForAsync(content, ct))?.CurrentVersion;

        // 2. PERMISSION: the role gate on the route says who may reach it at all. This says whether
        // this caller may update this content type, which is the permission a rollback actually
        // exercises, since the write it performs is indistinguishable from PUT /api/contents/{id}.
        // Without it an Admin with no update grant on a type could rewrite an entry of that type by
        // restoring it, while being refused the history that lists what to restore.
        //
        // It runs before the stream is read, so a caller who may not write learns nothing about
        // what versions exist. See #447.
        var actor = await barakoCMS.Infrastructure.Auth.ActingCaller.LoadAsync(session, User, ct);
        if (actor == null || !await permissionResolver.CanPerformActionAsync(
                actor, content.ContentType, "update", content, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // 3. Fetch the event stream
        var events = await session.Events.FetchStreamAsync(req.Id, token: ct);

        // 4. Find the target event
        var targetEvent = events.FirstOrDefault(e => e.Id == req.VersionId);

        if (targetEvent == null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // 5. Extract data from the event
        var data = targetEvent.Data switch
        {
            ContentCreated created => created.Data,
            ContentUpdated updated => updated.Data,
            _ => null,
        };

        if (data == null)
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

        if (!await PassesUpdateGatesAsync(actor, content, data, userId, ct))
        {
            return;
        }

        // Rebuild SearchText using the current field-sensitivity definition so a rollback
        // cannot reintroduce values that are no longer Public into the searchable text.
        var definition = await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, content.ContentType, ct);

        var searchText = BuildSearchText(definition, data);

        // 5. Create a new update event with the old data and rebuilt SearchText
        var rollbackEvent = new ContentUpdated(req.Id, data, userId, searchText, DateTime.UtcNow);

        // 6. Append the new event and update the document together
        await CommitRollbackAsync(content, rollbackEvent, loadedVersion, ct);

        // 8. Return the new state, through the read rules a GET applies: the caller is shown what
        // they may read of it and nothing more. Rollback asks for update, not read, so a caller no
        // Read rule grants this entry gets it back with no data, as a GET would refuse them.
        var response = await ReadBackAsync(actor, content, definition, ct);

        await Send.ResponseAsync(response, cancellation: ct);
    }

    private async Task<Guid?> ReadUserIdAsync(CancellationToken ct)
    {
        var id = barakoCMS.Infrastructure.Auth.ActingCaller.IdOf(User);
        if (id is not { } userId)
        {
            await Send.UnauthorizedAsync(ct);
            return null;
        }

        return userId;
    }

    /// <summary>Runs the gates an update runs on the restored data. Answers the refusal itself and returns false when one fails.</summary>
    private async Task<bool> PassesUpdateGatesAsync(
        barakoCMS.Models.User actor, barakoCMS.Models.Content content, Dictionary<string, object> data, Guid userId, CancellationToken ct)
    {
        // WRITE-PATH SENSITIVITY: a caller who may not see a field may not change it, and restoring
        // an old value is a change. Reverts any such field to what is stored, and refuses a restore
        // that changes a field the Update rule does not let the caller set.
        await PutBackOwnedFieldsAsync(content, data, ct);

        // The update rule has to hold for the entry as restored too, or restoring an old version
        // moves the entry out of the rows the rule limits this caller to.
        if (!await permissionResolver.AllowsWrittenEntryAsync(actor, content, "update", data, ct))
        {
            await Send.ForbiddenAsync(ct);
            return false;
        }

        var validationResult = await Resolve<barakoCMS.Infrastructure.Services.IContentValidatorService>()
            .ValidateAsync(content.ContentType, data, existing: content, caller: User);
        if (!validationResult.IsValid)
        {
            await RefuseRestoreAsync(validationResult.Errors, ct);
            return false;
        }

        var hookErrors = await Resolve<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(content.ContentType, content.Id, data, content.Data, userId, ct);
        if (hookErrors.Count > 0)
        {
            await RefuseRestoreAsync(hookErrors, ct);
            return false;
        }

        return true;
    }

    private async Task RefuseRestoreAsync(IEnumerable<string> errors, CancellationToken ct)
    {
        foreach (var error in errors)
        {
            AddError($"This version cannot be restored: {error}");
        }

        await Send.ErrorsAsync(400, ct);
    }

    private static string BuildSearchText(ContentTypeDefinition? definition, Dictionary<string, object> data)
    {
        var publicFields = definition?.Fields
            .Where(barakoCMS.Core.Validation.PublicFields.InSearchText)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return barakoCMS.Core.Validation.SearchTextBuilder.Build(data, publicFields);
    }

    /// <summary>
    /// Puts back what an old version may not change: fields the caller cannot see, and the entry's
    /// number, which is the server's and is kept whatever the old version held.
    /// </summary>
    private async Task PutBackOwnedFieldsAsync(barakoCMS.Models.Content content, Dictionary<string, object> data, CancellationToken ct)
    {
        await Resolve<barakoCMS.Core.Interfaces.ISensitivityService>()
            .ApplyWriteAsync(content, data, HttpContext, ct);

        var definition = await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, content.ContentType, ct);
        if (definition is not null)
            barakoCMS.Infrastructure.Services.ContentValidatorService.KeepStoredNumber(definition, data, content, refuseDifferent: false);
    }

    /// <summary>
    /// Appends under an expected-version check: the restore is a whole-document write built from a
    /// document loaded earlier in the request, so a plain append would overwrite an edit that landed
    /// in between. 409, as change-status answers.
    /// </summary>
    private async Task CommitRollbackAsync(barakoCMS.Models.Content content, ContentUpdated rollbackEvent, Guid? loadedVersion, CancellationToken ct)
    {
        try
        {
            await contentWriter.AppendOptimisticAsync(content, new object[] { rollbackEvent }, ct);

            // Bound to the version read at load, and registered after the append: the writer
            // reloads the committed document inside it, which would otherwise replace this.
            if (loadedVersion is { } expected)
            {
                session.UpdateExpectedVersion(content, expected);
            }

            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency")
            || ex.GetType().Name.Contains("UnexpectedMaxEventId"))
        {
            ThrowError("The content was changed by another writer. Please refresh and try again.", 409);
        }
    }

    /// <summary>The restored entry as a read by this caller would show it, recorded as a read when it shows anything above Public.</summary>
    private async Task<RollbackResponse> ReadBackAsync(
        barakoCMS.Models.User actor, barakoCMS.Models.Content content, ContentTypeDefinition? definition, CancellationToken ct)
    {
        var response = RollbackResponse.From(content);
        response.Data = new Dictionary<string, object>();
        if (!await permissionResolver.CanPerformActionAsync(actor, content.ContentType, "read", content, ct))
        {
            return response;
        }

        response.Data = new Dictionary<string, object>(content.Data);
        var sensitivity = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        if (await sensitivity.ApplyAsync(content, response.Data, HttpContext, ct))
        {
            response.ContentType = "HIDDEN";
        }

        await barakoCMS.Infrastructure.Audit.SensitiveReadAudit.RecordAsync(
            HttpContext,
            [await barakoCMS.Infrastructure.Audit.SensitiveReadAudit.DescribeAsync(
                content, definition, response.Data, sensitivity, HttpContext, ct)],
            ct);
        return response;
    }
}
