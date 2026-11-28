using barakoCMS.Features.ContentType.ChangeField;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.AbandonFieldChange;

internal sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>POST /api/content-types/field-changes/{id}/abandon: stops a stuck recompute and frees its type.</summary>
/// <remarks>
/// For a recompute whose job keeps failing, so the type is never write-locked for good by one entry
/// that will not save. Only a recompute is abandoned: stopping it part way is safe, because an
/// entry not yet rewritten keeps its old computed value and the next write to it works the value
/// out again from the current formula. The other kinds leave state a stop cannot undo (a rename's
/// values under two names, a convert's mixed types, a removed field's values that come back when the
/// name is added again), so they answer 409 and are resumed instead. A completed or already
/// abandoned change is a 409, and so is one whose job is still queued or running, which abandoning
/// would race. Abandoning a retry frees the change it was started for to be retried again.
/// Another tenant's change, like one that does not exist, is a 404.
/// The check and the store sit under the type's lock, the one a change starts under and a batch
/// takes, so an abandon never lands between a batch's read and its commit.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, FieldChangeResponse>
{
    public override void Configure()
    {
        Post("/api/content-types/field-changes/{id}/abandon");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Not tracked, so the load after the lock reads the change as the lock holder left it.
        var type = await session.Query<FieldChange>().Where(c => c.Id == req.Id).Select(c => c.ContentType).FirstOrDefaultAsync(ct);
        if (type is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await FieldChanges.LockTypeAsync(session, type, ct);
        if (await session.LoadAsync<FieldChange>(req.Id, ct) is not { } change)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (Refusal(change) is { } refusal)
        {
            AddError(refusal);
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        if (await FieldChanges.HasLiveJobAsync(session, change.Id, ct))
        {
            AddError("This change's job is still queued or running, and carries on by itself. Abandon it once the queue has given up on it.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        await AbandonAsync(change, ct);
        await Send.OkAsync(FieldChangeResponse.From(change), ct);
    }

    /// <summary>Why the change cannot be abandoned as it stands, or null when it can.</summary>
    private static string? Refusal(FieldChange change) =>
        change.State == FieldChangeState.Completed ? "This change has completed. There is nothing left to abandon."
        : change.State == FieldChangeState.CompletedWithSkips ? "This change completed with skips. There is nothing left to abandon."
        : change.State == FieldChangeState.Abandoned ? "This change was already abandoned."
        : change.Kind != FieldChangeKind.Recompute ? "Only a recompute can be abandoned. Resume this change instead."
        : null;

    /// <summary>
    /// Frees the change a retry was started for, so it can be retried again. Read under the same
    /// type lock, and stored in the same commit as the abandon.
    /// </summary>
    private async Task FreeOriginAsync(FieldChange retry, DateTimeOffset now, CancellationToken ct)
    {
        if (retry.RetryOf is { } originId
            && await session.LoadAsync<FieldChange>(originId, ct) is { } origin
            && origin.RetriedBy == retry.Id)
        {
            origin.RetriedBy = null;
            origin.UpdatedAt = now;
            session.Store(origin);
        }
    }

    private async Task AbandonAsync(FieldChange change, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var actorId = ActingCaller.IdOf(User);
        change.State = FieldChangeState.Abandoned;
        change.AbandonedAt = change.UpdatedAt = now;
        change.AbandonedBy = actorId;
        session.Store(change);
        await FreeOriginAsync(change, now, ct);

        // Ids and counts only: no entry's value is read or written here.
        await AuditLog.RecordAsync(session, tenant.Slug, "contenttype.field_change.abandoned", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(FieldChange), targetId: change.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = change.ContentType,
                ["field"] = change.Field,
                ["kind"] = change.Kind.ToString(),
                ["processed"] = change.Processed,
                ["total"] = change.Total,
            }, ct: ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ConcurrencyConflict.Is(ex))
        {
            ThrowError("The change moved just now. Nothing was changed. Send the request again.", StatusCodes.Status409Conflict);
        }
    }
}
