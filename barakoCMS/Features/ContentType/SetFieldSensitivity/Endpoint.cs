using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.ContentType.SetFieldSensitivity;

/// <summary>
/// PUT /api/content-types/{name}/fields/{field}/sensitivity, which changes one field's sensitivity
/// and brings the entries already written under the old level into line with it.
/// </summary>
/// <remarks>
/// Its own endpoint rather than a general content-type update, for the reason
/// <c>SetPublicDelivery</c> is: this decides who may read data that already exists, which is worth
/// making on purpose and worth being able to audit, rather than something that rides along inside a
/// larger edit. A general update is a separate decision, because field names, field types and a
/// reference target are load-bearing once entries exist.
///
/// The two directions are not symmetric, and treating them as one operation is the mistake this is
/// shaped to avoid.
///
/// RAISING (Public towards Hidden) is safe for the future and does nothing about the past. The value
/// stops being served, which is what the caller wants. It is still in the entry's data, in every
/// backup and in the event stream, and nothing here removes it from any of those.
///
/// Raising is also not finished when the definition is updated. Anonymous search matches against
/// <c>Content.SearchText</c>, a derived column built from the fields that were Public when the entry
/// was last written. Flipping the definition changes what is returned and not what is matched, so a
/// caller could still search for a value they may no longer read and learn which entries contain it,
/// one guess at a time. The search text is rebuilt here for that reason, and it is rebuilt BEFORE
/// the definition changes so no window exists where the old text is still matchable under the new
/// level.
///
/// LOWERING is a disclosure. Every value written while the field was masked becomes readable to
/// everyone who can read the type, retroactively, and to anonymous callers as well when the type is
/// publicly deliverable. It is allowed, because refusing it would make raising a one-way door and a
/// field marked Sensitive by mistake would need direct database access to recover, which is the
/// failure <c>SetPublicDelivery</c> exists to answer. It is allowed only with
/// <c>acknowledgeDisclosure</c>, and it is recorded under its own audit action so it can be alerted
/// on. Lowering updates the definition FIRST and rebuilds the search text after, which is the same
/// rule the other way round: whichever step exposes less goes first.
///
/// It deliberately does not run <c>IContentTypeValidatorService</c>. That validates a whole type on
/// create and has no rules about sensitivity, so all it could do here is refuse the request for
/// something unrelated: a type created before the "a reference must name its target" rule existed
/// would fail it, and masking a leaking field on that type would become impossible.
/// </remarks>
internal class Endpoint : Endpoint<Request, Response>
{
    /// <summary>Entries per batch while rebuilding search text.</summary>
    private const int BatchSize = 200;

    private readonly IDocumentSession _session;
    private readonly IContentWriter _writer;
    private readonly barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache _openApiCache;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly IContentSourcingPolicy _sourcing;

    public Endpoint(
        IDocumentSession session,
        IContentWriter writer,
        barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        IContentSourcingPolicy sourcing)
    {
        _session = session;
        _writer = writer;
        _openApiCache = openApiCache;
        _tenant = tenant;
        _sourcing = sourcing;
    }

    public override void Configure()
    {
        Put("/api/content-types/{name}/fields/{field}/sensitivity");
        Roles("Admin", "SuperAdmin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;
        var fieldName = Route<string>("field") ?? string.Empty;

        var def = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == name, ct);

        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Case-insensitive, matching how every other reader of the schema resolves a field name.
        var field = def.Fields.FirstOrDefault(
            f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            // Not a 400. A field that is not on the type and a type that is not there give the same
            // answer, because saying which would let a caller enumerate a type's fields from here.
            await Send.NotFoundAsync(ct);
            return;
        }

        var from = field.Sensitivity;
        var to = req.Sensitivity;
        var lowering = to < from;

        if (to == SensitivityLevel.Public
            && (req.VisibleToRoles is { Count: > 0 } || req.Mask is not null and not FieldMask.Default))
        {
            // Refused rather than dropped. A caller who sends both believes they have restricted the
            // field to a role list, and a silent drop leaves them believing it.
            AddError("A Public field cannot carry visibleToRoles or a mask: everyone reads it.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // Decision 2 of #230, enforced here as well as at creation. An event-sourced type may not
        // hold non-Public fields, and a type that could not be created with one must not be able to
        // acquire one afterwards: raising a field on it would put personal data into an append-only
        // stream that nothing can erase it from. Lowering back to Public is still allowed, so a field
        // is never stranded.
        if (to != SensitivityLevel.Public && await _sourcing.IsEventSourcedAsync(def.Name, ct))
        {
            AddError(
                $"'{def.Name}' is event sourced, so its fields have to stay Public. Raising "
                + $"'{field.Name}' to {to} would put values into an append-only stream that no "
                + "erasure request can take them out of.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (lowering && !req.AcknowledgeDisclosure)
        {
            var affected = await _session.Query<ContentDoc>().CountAsync(c => c.ContentType == def.Name, ct);
            var reach = def.IsPubliclyDeliverable
                ? "everyone who can read this type, and anonymous callers through the public delivery API"
                : "everyone who can read this type";

            AddError(
                $"Lowering '{field.Name}' from {from} to {to} makes its value readable by {reach}, "
                + $"for {affected} existing {(affected == 1 ? "entry" : "entries")} as well as new "
                + "ones. Resend with acknowledgeDisclosure set to true.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : Guid.Empty;

        // The public field set as it will be once the change lands, which is what the search text has
        // to be built from in both directions.
        var publicFields = def.Fields
            .Where(f => f == field ? to == SensitivityLevel.Public : f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reindexed = 0;

        if (!lowering)
            reindexed = await RebuildSearchTextAsync(def.Name, field.Name, from, to, publicFields, actorId, ct);

        field.Sensitivity = to;
        field.VisibleToRoles = to == SensitivityLevel.Public
            ? new List<string>()
            : req.VisibleToRoles ?? new List<string>();
        field.Mask = to == SensitivityLevel.Public ? FieldMask.Default : req.Mask ?? FieldMask.Default;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        _session.Store(def);

        // Lowering is a disclosure, so it gets an action of its own. Alerting on it should not mean
        // reading the metadata of every sensitivity change.
        await AuditLog.RecordAsync(
            _session,
            _tenant.Slug,
            lowering ? "contenttype.field.sensitivity.lowered" : "contenttype.field.sensitivity.changed",
            actorId == Guid.Empty ? null : actorId,
            User.FindFirst("Username")?.Value,
            targetType: "ContentType",
            targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["field"] = field.Name,
                ["from"] = from.ToString(),
                ["to"] = to.ToString(),
                ["publiclyDeliverable"] = def.IsPubliclyDeliverable,
            },
            ct: ct);

        await _session.SaveChangesAsync(ct);

        if (lowering)
            reindexed = await RebuildSearchTextAsync(def.Name, field.Name, from, to, publicFields, actorId, ct);

        // The delivery OpenAPI document lists only the Public fields of a type, so this changed it.
        _openApiCache.Invalidate(_tenant.Slug);

        await Send.OkAsync(new Response
        {
            Name = def.Name,
            Field = field.Name,
            From = from,
            To = to,
            VisibleToRoles = field.VisibleToRoles,
            Mask = field.Mask,
            EntriesReindexed = reindexed,
        }, ct);
    }

    /// <summary>
    /// Rewrites <c>SearchText</c> for every entry of the type whose text the new field set changes.
    /// </summary>
    /// <remarks>
    /// Synchronous and proportional to the number of entries of the type, which is the trade taken
    /// deliberately: a background rebuild would answer 200 while the value was still matchable
    /// anonymously, and the caller would have nothing telling them when it stopped being.
    ///
    /// Batched on the id so the working set stays bounded, and each batch commits on its own. A run
    /// that dies partway leaves the entries it reached correct and the rest unchanged, and rerunning
    /// the same request finishes the job.
    ///
    /// An entry with no search text at all is left alone. It was never indexed, so there is nothing
    /// to scrub, and indexing it here would make content searchable that was not searchable before,
    /// which is the seeder's backfill and not this endpoint's business.
    ///
    /// The rewrite goes through the writer so it lands on the stream as well as the document. A bare
    /// store would hold only until the next projection rebuild, which replays the last write event
    /// and puts the old text back.
    /// </remarks>
    private async Task<int> RebuildSearchTextAsync(
        string contentType,
        string fieldName,
        SensitivityLevel from,
        SensitivityLevel to,
        HashSet<string> publicFields,
        Guid actorId,
        CancellationToken ct)
    {
        var updated = 0;
        Guid? lastId = null;

        while (true)
        {
            var query = _session.Query<ContentDoc>().Where(c => c.ContentType == contentType);

            if (lastId.HasValue)
                query = query.Where(c => c.Id > lastId.Value);

            var batch = await query.OrderBy(c => c.Id).Take(BatchSize).ToListAsync(ct);

            if (batch.Count == 0)
                break;

            lastId = batch[^1].Id;

            var staged = false;
            foreach (var content in batch)
            {
                if (content.SearchText is null)
                    continue;

                var rebuilt = string.Join(
                    ' ',
                    content.Data
                        .Where(kv => publicFields.Contains(kv.Key))
                        .Select(kv => kv.Value?.ToString())
                        .Where(v => !string.IsNullOrWhiteSpace(v)));

                if (string.Equals(rebuilt, content.SearchText, StringComparison.Ordinal))
                    continue;

                await _writer.AppendAsync(content, new ContentFieldSensitivityChanged(
                    content.Id, fieldName, from, to, rebuilt, actorId), ct);

                staged = true;
                updated++;
            }

            if (staged)
                await _session.SaveChangesAsync(ct);
        }

        return updated;
    }
}
