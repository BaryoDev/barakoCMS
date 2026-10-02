using FastEndpoints;
using Marten;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetFieldPresentation;

/// <summary>
/// PUT /api/content-types/{name}/fields/{field}/presentation, which sets or clears the editor
/// hint, the section and the role of one field.
/// </summary>
/// <remarks>
/// Its own endpoint for the reason <c>SetFieldOptions</c> is: there is no general field update. It
/// is how a field stored before these members existed gets them, and no entry is read or written,
/// because none of the three changes what an entry may hold.
///
/// Only the three members are validated, not the whole type and not the field's name, so a type
/// created before some later rule existed can still take a hint.
///
/// A role another field of the type holds is refused, not moved. Which field is the title decides
/// what a feed reader shows, so taking it from one field is a call of its own.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/fields/{field}/presentation");
        // How a field is edited is modelling, the same gate as adding the field.
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        var fieldName = Route<string>("field") ?? string.Empty;

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        var field = def?.Fields.FirstOrDefault(
            f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase));

        if (def is null || field is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var proposed = new FieldDefinition
        {
            Name = field.Name,
            DisplayName = field.DisplayName,
            Type = field.Type,
            Editor = req.Editor,
            Section = req.Section,
            Role = req.Role,
        };

        var errors = FieldPresentation.DefinitionErrors(proposed);

        // Asked only of a role the check above accepted, so the name repeated here is one of the
        // vocabulary and not whatever was sent.
        if (errors.Count == 0 && req.Role is not null)
        {
            var holder = def.Fields.FirstOrDefault(
                f => !ReferenceEquals(f, field) && string.Equals(f.Role, req.Role, StringComparison.Ordinal));

            if (holder is not null)
            {
                errors.Add($"The role '{req.Role}' is held by '{holder.Name}', and one field of a type holds a role. "
                    + "Clear it there first.");
            }
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var changed = !string.Equals(field.Editor, req.Editor, StringComparison.Ordinal)
                      || !string.Equals(field.Section, req.Section, StringComparison.Ordinal)
                      || !string.Equals(field.Role, req.Role, StringComparison.Ordinal);

        if (changed)
        {
            var before = Describe(field);

            field.Editor = req.Editor;
            field.Section = req.Section;
            field.Role = req.Role;
            def.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(def);

            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(
                session,
                tenant.Slug,
                "contenttype.field.presentation.changed",
                actorId,
                User.FindFirst("Username")?.Value,
                targetType: "ContentType",
                targetId: def.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = def.Name,
                    ["field"] = field.Name,
                    ["from"] = before,
                    ["to"] = Describe(field),
                },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response
        {
            Name = def.Name,
            Field = field.Name,
            Editor = field.Editor,
            Section = field.Section,
            Role = field.Role,
        }, ct);
    }

    private static string Describe(FieldDefinition field) =>
        $"editor {field.Editor ?? "none"}, section {field.Section ?? "none"}, role {field.Role ?? "none"}";
}
