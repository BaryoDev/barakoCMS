using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
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
/// one guess at a time. The search text is rebuilt for that reason, and it is rebuilt BEFORE the
/// definition changes so no window exists where the old text is still matchable under the new
/// level. Both happen as one <see cref="FieldChange"/>: applied before the answer for a type that
/// fits in one batch, and by the job queue for a larger one, with the change in the response.
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
internal class Endpoint(
    IDocumentSession session,
    IDocumentStore store,
    IConfiguration configuration,
    barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache openApiCache,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    IContentSourcingPolicy sourcing) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/fields/{field}/sensitivity");
        // Sensitivity is what decides whether a field is scrubbed on the way out to an anonymous
        // caller, so it is a disclosure decision rather than a modelling one.
        Definition.RequireCapability(SystemCapabilities.ManagePublicDelivery, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;
        var fieldName = Route<string>("field") ?? string.Empty;

        var def = await session.Query<ContentTypeDefinition>()
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

        if (to == SensitivityLevel.Public && barakoCMS.Core.Validation.TokenFields.IsToken(field.Type))
        {
            AddError(
                $"'{field.Name}' is a token and cannot be made Public. Keep it Hidden or Sensitive, "
                + "and name the roles that may read it in visibleToRoles.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

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
        if (to != SensitivityLevel.Public && await sourcing.IsEventSourcedAsync(def.Name, ct))
        {
            AddError(
                $"'{def.Name}' is event sourced, so its fields have to stay Public. Raising "
                + $"'{field.Name}' to {to} would put values into an append-only stream that no "
                + "erasure request can take them out of.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        // A uniqueness refusal tells the caller that some entry holds the value they sent. For a
        // field they may not read that is a way to test what it holds, so a field a rule names
        // stays Public. Lowering is never refused here.
        if (to != SensitivityLevel.Public
            && barakoCMS.Core.Validation.UniquenessRules.NamedBy(def, field.Name) is { Count: > 0 } rules)
        {
            AddError(
                $"'{field.Name}' is compared by the uniqueness rule {string.Join(", ", rules.Select(r => $"'{r}'"))}, "
                + $"so it has to stay Public. Take it out of the rule with PUT /api/content-types/{def.Name}/uniqueness first.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (lowering && !req.AcknowledgeDisclosure)
        {
            var affected = await session.Query<ContentDoc>().CountAsync(c => c.ContentType == def.Name, ct);
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

        // The roles as stored (ids) and as answered (names), worked out now so the change carries
        // exactly what this request asked for. A copy, so the stored field is not touched until the
        // change's definition step runs.
        var target = new FieldDefinition
        {
            VisibleToRoles = to == SensitivityLevel.Public ? new List<string>() : req.VisibleToRoles ?? new List<string>(),
        };
        await barakoCMS.Core.RoleReferences.ToIdsAsync(session, [target], ct);
        var answered = new FieldDefinition { VisibleToRoles = [.. target.VisibleToRoles] };
        var roles = await RoleAudit.RoleListsAsync(session, [field.VisibleToRoles?.ToList() ?? new List<string>(), target.VisibleToRoles], [answered], ct);

        var change = NewChange(def, field, req, target.VisibleToRoles, actorId);
        if (await FieldChanges.StartAsync(session, change, ct, inlineWhenSmall: true) is { } busy)
        {
            RefuseBusy(busy);
        }

        // Lowering is a disclosure, so it gets an action of its own. Alerting on it should not mean
        // reading the metadata of every sensitivity change.
        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
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
                ["visibleToRoleIdsFrom"] = roles[0].Ids,
                ["visibleToRolesFrom"] = roles[0].Names,
                ["visibleToRoleIdsTo"] = roles[1].Ids,
                ["visibleToRolesTo"] = roles[1].Names,
                ["maskFrom"] = field.Mask.ToString(),
                ["maskTo"] = change.Mask.ToString()!,
                ["publiclyDeliverable"] = def.IsPubliclyDeliverable,
                ["change"] = change.Id.ToString(),
            },
            ct: ct);

        await session.SaveChangesAsync(ct);

        var applied = await RunInlineAsync(change, ct);

        // The delivery OpenAPI document lists only the Public fields of a type, so this changed it.
        openApiCache.Invalidate(tenant.Slug);

        await Send.OkAsync(new Response
        {
            Name = def.Name,
            Field = field.Name,
            From = from,
            To = to,
            VisibleToRoles = answered.VisibleToRoles,
            Mask = change.Mask ?? FieldMask.Default,
            EntriesReindexed = applied.State == FieldChangeState.Completed ? applied.Processed : 0,
            Change = barakoCMS.Features.ContentType.ChangeField.FieldChangeResponse.From(applied),
        }, ct);
    }

    /// <summary>The change this request asks for, carrying the roles as stored.</summary>
    private static FieldChange NewChange(ContentTypeDefinition def, FieldDefinition field, Request req, List<string> roleIds, Guid actorId) => new()
    {
        ContentType = def.Name,
        Field = field.Name,
        Kind = FieldChangeKind.Sensitivity,
        FromSensitivity = field.Sensitivity,
        ToSensitivity = req.Sensitivity,
        VisibleToRoles = roleIds,
        Mask = req.Sensitivity == SensitivityLevel.Public ? FieldMask.Default : req.Mask ?? FieldMask.Default,
        DefinitionFirst = req.Sensitivity < field.Sensitivity,
        RequestedBy = actorId,
    };

    private void RefuseBusy(FieldChange running)
    {
        AddError(FieldChanges.Busy(running));
        ThrowIfAnyErrors(StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// Applies the change before answering when the type fits in one batch, and otherwise answers
    /// with the change still running on its job.
    /// </summary>
    /// <remarks>
    /// The steps are the same either way, and their order is the one this endpoint always had: when
    /// raising, the search text is rebuilt before the definition changes, so no window exists where
    /// the old text is still matchable under the new level; when lowering, the definition changes
    /// first. Inline for a small type, because a client reading the type straight after this answer
    /// expects the new level, and a job for a large one, because rebuilding a whole type used to hold
    /// the request open for as long as it took. The small change's job was queued with it, to start a
    /// minute later, so a run cut short here, by a failure or by the client going away, is finished
    /// by the job rather than leaving the type's writes refused.
    /// </remarks>
    private async Task<FieldChange> RunInlineAsync(FieldChange change, CancellationToken ct)
    {
        if (FieldChanges.RunsInline(change))
        {
            try
            {
                if (await FieldChanges.RunAsync(store, configuration, session.TenantId, change.Id, maxBatches: null, ct) is
                    { State: FieldChangeState.Completed } done)
                {
                    return done;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await FieldChanges.RecordErrorAsync(store, session.TenantId, change.Id, e, ct);
            }
        }

        await using var read = store.QuerySession(session.TenantId);
        return await read.LoadAsync<FieldChange>(change.Id, ct) ?? change;
    }
}
