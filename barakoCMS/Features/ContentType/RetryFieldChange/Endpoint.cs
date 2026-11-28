using barakoCMS.Features.ContentType.ChangeField;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.RetryFieldChange;

internal sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>POST /api/content-types/field-changes/{id}/retry: recomputes the entries a recompute skipped.</summary>
/// <remarks>
/// Starts a new recompute on the same type and field, through the start path every recompute
/// uses, and links the two. When the skipped ids were all recorded it visits only those entries;
/// when more were skipped than a change records, it makes a full pass, where an entry that comes
/// out the same writes nothing. Only a change that completed with skips is retried, and once: a
/// second retry is a 409 that names the first. So is a retry while a change runs on the type.
/// Another tenant's change, like one that does not exist, is a 404. The check and the store sit
/// under the type's lock, so two retries cannot both start.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, FieldChangeResponse>
{
    public override void Configure()
    {
        Post("/api/content-types/field-changes/{id}/retry");
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
        if (await session.LoadAsync<FieldChange>(req.Id, ct) is not { } old)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (Refusal(old) is { } refusal)
        {
            AddError(refusal);
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        var retry = new FieldChange
        {
            ContentType = old.ContentType,
            Field = old.Field,
            Kind = FieldChangeKind.Recompute,
            RequestedBy = ActingCaller.IdOf(User) ?? Guid.Empty,
            RetryOf = old.Id,
            EntryIds = RecordedIds(old),
        };
        if (await FieldChanges.StartAsync(session, retry, ct) is { } busy)
        {
            AddError(FieldChanges.Busy(busy));
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        old.RetriedBy = retry.Id;
        old.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(old);
        await AuditAsync(old, retry, ct);
        await SaveAsync(ct);
        await Send.ResponseAsync(FieldChangeResponse.From(retry), 202, ct);
    }

    /// <summary>Why the change cannot be retried as it stands, or null when it can.</summary>
    private static string? Refusal(FieldChange change) =>
        change.State != FieldChangeState.CompletedWithSkips ? "Only a change that completed with skips is retried."
        : change.RetriedBy is { } by ? $"This change was already retried, as {by}."
        : null;

    /// <summary>The skipped ids, when every skip was recorded. Null otherwise, which is a full pass.</summary>
    private static List<Guid>? RecordedIds(FieldChange change) =>
        change.Skipped is { Count: > 0 } skipped && skipped.Count == change.SkippedCount
            ? skipped.Select(k => k.EntryId).ToList()
            : null;

    /// <summary>Ids and counts only: no entry's value is read or written here.</summary>
    private Task AuditAsync(FieldChange old, FieldChange retry, CancellationToken ct) =>
        AuditLog.RecordAsync(session, tenant.Slug, "contenttype.field_change.retried", ActingCaller.IdOf(User),
            User.FindFirst("Username")?.Value,
            targetType: nameof(FieldChange), targetId: retry.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = old.ContentType,
                ["field"] = old.Field,
                ["retryOf"] = old.Id.ToString(),
                ["change"] = retry.Id.ToString(),
                ["skippedCount"] = old.SkippedCount,
                ["total"] = retry.Total,
                ["fullPass"] = retry.EntryIds is null,
            }, ct: ct);

    private async Task SaveAsync(CancellationToken ct)
    {
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
