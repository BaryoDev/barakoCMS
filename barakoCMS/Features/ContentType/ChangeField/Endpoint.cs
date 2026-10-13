using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.ChangeField;

/// <summary>
/// POST /api/content-types/{name}/fields/{field}/change: changes one field of a type that may
/// already hold entries, and says what kind of change it was.
/// </summary>
/// <remarks>
/// A label is additive: it touches no entry and is applied at once (200). A rename, a convert (a
/// new type, with a mapping for the stored values) and a remove touch every entry, so each is a
/// <see cref="FieldChange"/> applied by a job (202), whose progress
/// <c>GET /api/content-types/field-changes/{id}</c> reports and which
/// <c>POST /api/content-types/field-changes/{id}/resume</c> runs again after it stopped.
///
/// While a change runs, the field is read under its old name: the job copies every value to the
/// new name first, then renames the field, then drops the old key. Entry writes to the type are
/// refused until it completes, so no write lands between two batches in a shape the job has
/// already passed. One change at a time per type.
///
/// A change that drops stored values is destructive: a remove always, and a convert when some value
/// neither maps to nor reads as a value of the new type. It is refused unless the request says
/// <c>allowDestructive</c>, and the refusal names how many entries hold a value it would drop.
/// </remarks>
internal sealed class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/content-types/{name}/fields/{field}/change");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var lowered = (Route<string>("name") ?? string.Empty).ToLower();
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        var fieldName = Route<string>("field") ?? string.Empty;
        var field = definition?.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));
        if (definition is null || field is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.NewName is not null || req.Remove || req.ToType is not null)
        {
            RefuseUnlessPublic(field);
        }

        if (req.NewName is not null)
        {
            await RenameAsync(definition, field, req.NewName.Trim(), ct);
            return;
        }

        if (req.Remove || req.ToType is not null)
        {
            await ReshapeAsync(definition, field, req, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(req.DisplayName))
        {
            AddError("Send newName, or displayName on its own.");
            ThrowIfAnyErrors();
        }

        field.DisplayName = req.DisplayName!.Trim();
        definition.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(definition);
        await AuditAsync(definition, field.Name, "additive", null, ct);
        await session.SaveChangesAsync(ct);
        await Send.OkAsync(new Response { Classification = "additive" }, ct);
    }

    private async Task RenameAsync(ContentTypeDefinition definition, FieldDefinition field, string newName, CancellationToken ct)
    {
        if (string.Equals(newName, field.Name, StringComparison.OrdinalIgnoreCase))
        {
            AddError($"'{newName}' differs from '{field.Name}' only in case, and fields are matched ignoring case.");
            ThrowIfAnyErrors();
        }

        // The type as it would be, through the same validator create runs, so a name that is not
        // PascalCase or that another field already has is refused for the same reason.
        var renamed = definition.Fields
            .Select(f => f == field ? new FieldDefinition { Name = newName, DisplayName = f.DisplayName, Type = "string" } : f)
            .ToList();
        // Only the errors the rename brings: a type created before some rule existed may break it
        // already, and that is no reason to refuse a rename.
        var (_, before) = validator.Validate(definition.Name, definition.DisplayName, definition.Fields, definition.Fields);
        var (_, after) = validator.Validate(definition.Name, definition.DisplayName, renamed, definition.Fields);
        foreach (var error in after.Except(before))
        {
            AddError(error);
        }

        ThrowIfAnyErrors();

        var change = new FieldChange
        {
            ContentType = definition.Name,
            Field = field.Name,
            Kind = FieldChangeKind.Rename,
            NewName = newName,
            RequestedBy = ActorId(),
        };
        if (await FieldChanges.StartAsync(session, change, ct) is { } busy)
        {
            RefuseBusy(busy);
        }

        await AuditAsync(definition, field.Name, "rename", change, ct);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(new Response { Classification = "rename", Change = FieldChangeResponse.From(change) }, 202, ct);
    }

    /// <summary>
    /// Refuses a rename, a convert or a remove of a field that is not Public, or that holds a token.
    /// </summary>
    /// <remarks>
    /// Reads mask a field by the name the type declares. While a rename or a remove runs, and in
    /// every history event written before it, the value sits under a name the type no longer
    /// declares, where nothing masks it. A field has to be Public, so there is nothing to mask,
    /// before its values move.
    /// </remarks>
    private void RefuseUnlessPublic(FieldDefinition field)
    {
        if (barakoCMS.Core.Validation.TokenFields.IsToken(field.Type))
        {
            AddError($"'{field.Name}' holds a token. A token field is not renamed, converted or removed by a field change.");
        }
        else if (field.Sensitivity != SensitivityLevel.Public)
        {
            AddError($"'{field.Name}' is {field.Sensitivity}. Its values would sit under a name the type does not declare, "
                + "where reads do not mask them, so only a Public field is renamed, converted or removed.");
        }

        ThrowIfAnyErrors();
    }

    /// <summary>A convert or a remove: refused while the type's rules name the field, and when it drops values, unless allowed.</summary>
    private async Task ReshapeAsync(ContentTypeDefinition definition, FieldDefinition field, Request req, CancellationToken ct)
    {
        if (barakoCMS.Core.Validation.FieldNames.NamedBy(definition, field.Name) is { Count: > 0 } named)
        {
            AddError($"'{field.Name}' is named by {string.Join(", ", named)}. Take it out of them first.");
            ThrowIfAnyErrors();
        }

        var change = new FieldChange
        {
            ContentType = definition.Name,
            Field = field.Name,
            Kind = req.Remove ? FieldChangeKind.Remove : FieldChangeKind.Convert,
            ToType = req.ToType?.Trim().ToLowerInvariant(),
            Options = req.Options,
            Mapping = req.Mapping,
            RequestedBy = ActorId(),
        };

        if (change.Kind == FieldChangeKind.Convert)
        {
            ConvertErrors(definition, field, change);
        }

        change.ValuesLost = await ChangeFieldValues.LostAsync(session, definition, change, ct);
        var destructive = change.Kind == FieldChangeKind.Remove || change.ValuesLost > 0;
        if (destructive && !req.AllowDestructive)
        {
            var what = change.Kind == FieldChangeKind.Remove ? $"Removing '{field.Name}'" : $"Converting '{field.Name}' to {change.ToType}";
            AddError($"{what} drops the value {change.ValuesLost} {(change.ValuesLost == 1 ? "entry holds" : "entries hold")}. "
                + "Resend with allowDestructive set to true to go ahead.");
            ThrowIfAnyErrors();
        }

        var classification = destructive ? "destructive" : "convert";
        if (await FieldChanges.StartAsync(session, change, ct) is { } busy)
        {
            RefuseBusy(busy);
        }

        await AuditAsync(definition, field.Name, classification, change, ct);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(new Response { Classification = classification, Change = FieldChangeResponse.From(change) }, 202, ct);
    }

    /// <summary>Refuses a convert the field or its target cannot take, through the validator create runs.</summary>
    private void ConvertErrors(ContentTypeDefinition definition, FieldDefinition field, FieldChange change)
    {
        if (!barakoCMS.Core.Validation.FieldConversion.Targets.Contains(change.ToType!))
        {
            AddError($"A field converts to one of {string.Join(", ", barakoCMS.Core.Validation.FieldConversion.Targets)}.");
        }
        else if (!barakoCMS.Core.Validation.FieldConversion.Sources.Contains(field.Type.ToLowerInvariant()) || field.Multiple)
        {
            AddError($"'{field.Name}' is a {field.Type}{(field.Multiple ? " holding a list" : string.Empty)}, which does not convert. "
                + "A field holding one plain value does.");
        }
        else
        {
            var target = new FieldDefinition { Name = field.Name, DisplayName = field.DisplayName, Sensitivity = field.Sensitivity };
            barakoCMS.Core.Validation.FieldConversion.Retype(target, change);
            var fields = definition.Fields.Select(f => f == field ? target : f).ToList();
            var (valid, errors) = validator.Validate(definition.Name, definition.DisplayName, fields, definition.Fields);
            foreach (var error in valid ? [] : errors.Where(e => e.Contains($"'{field.Name}'", StringComparison.OrdinalIgnoreCase)))
            {
                AddError(error);
            }
        }

        ThrowIfAnyErrors();
    }

    private void RefuseBusy(FieldChange running)
    {
        AddError(FieldChanges.Busy(running));
        ThrowIfAnyErrors(StatusCodes.Status409Conflict);
    }

    private Guid ActorId() => Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : Guid.Empty;

    private Task AuditAsync(ContentTypeDefinition definition, string field, string classification, FieldChange? change, CancellationToken ct)
    {
        var metadata = new Dictionary<string, object> { ["field"] = field, ["classification"] = classification };
        if (change is not null)
        {
            metadata["change"] = change.Id.ToString();
            metadata["entries"] = change.Total;
            if (change.NewName is not null)
            {
                metadata["newName"] = change.NewName;
            }

            if (change.ToType is not null)
            {
                metadata["toType"] = change.ToType;
            }

            if (change.Kind is FieldChangeKind.Remove or FieldChangeKind.Convert)
            {
                metadata["valuesLost"] = change.ValuesLost;
            }
        }

        var actor = ActorId();
        return AuditLog.RecordAsync(session, tenant.Slug, "contenttype.field_changed", actor == Guid.Empty ? null : actor,
            User.FindFirst("Username")?.Value, targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
            metadata: metadata, ct: ct);
    }
}
