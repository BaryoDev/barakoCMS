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
/// A label is additive: it touches no entry and is applied at once (200). A rename touches every
/// entry, so it is a <see cref="FieldChange"/> applied by a job (202), whose progress
/// <c>GET /api/content-types/field-changes/{id}</c> reports and which
/// <c>POST /api/content-types/field-changes/{id}/resume</c> runs again after it stopped.
///
/// While a change runs, the field is read under its old name: the job copies every value to the
/// new name first, then renames the field, then drops the old key. Entry writes to the type are
/// refused until it completes, so no write lands between two batches in a shape the job has
/// already passed. One change at a time per type.
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

        if (await FieldChanges.RunningAsync(session, definition.Name, ct) is { } running)
        {
            AddError($"'{definition.Name}' already has a field change running ({running.Id}). Wait for it to complete.");
            ThrowIfAnyErrors(StatusCodes.Status409Conflict);
        }

        if (req.NewName is not null)
        {
            await RenameAsync(definition, field, req.NewName.Trim(), ct);
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
        var (valid, errors) = validator.Validate(definition.Name, definition.DisplayName, renamed, definition.Fields);
        foreach (var error in valid ? [] : errors.Where(e => e.Contains($"'{newName}'", StringComparison.OrdinalIgnoreCase)))
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
        await FieldChanges.StartAsync(session, change, ct);
        await AuditAsync(definition, field.Name, "rename", change, ct);
        await session.SaveChangesAsync(ct);
        await Send.ResponseAsync(new Response { Classification = "rename", Change = FieldChangeResponse.From(change) }, 202, ct);
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
        }

        var actor = ActorId();
        return AuditLog.RecordAsync(session, tenant.Slug, "contenttype.field_changed", actor == Guid.Empty ? null : actor,
            User.FindFirst("Username")?.Value, targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
            metadata: metadata, ct: ct);
    }
}
