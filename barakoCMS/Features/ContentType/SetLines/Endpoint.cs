using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.SetLines;

/// <summary>
/// PUT /api/content-types/{name}/lines, which sets or clears the child type whose entries are a
/// type's lines.
/// </summary>
/// <remarks>
/// Opt in per type: a type with no lines declared is created exactly as before. Declaring lines
/// changes nothing already stored. Only a create that carries lines uses it.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/lines");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
        Definition.RefusesWhileFieldChangeRuns();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);
        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        LinesDefinition? lines = null;
        if (req.Lines is { } asked)
        {
            var childName = barakoCMS.Core.ContentTypeName.Normalize(asked.ContentType ?? string.Empty);
            var child = string.IsNullOrEmpty(childName)
                ? null
                : await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == childName, ct);

            var errors = Errors(def, child, asked);
            foreach (var error in errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
            lines = new LinesDefinition
            {
                ContentType = child!.Name,
                ParentField = child.Fields.First(f => string.Equals(f.Name, asked.ParentField, StringComparison.OrdinalIgnoreCase)).Name,
            };
        }

        var before = def.Lines?.ContentType ?? "none";
        def.Lines = lines;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(session, tenant.Slug, "contenttype.lines.changed", actorId, User.FindFirst("Username")?.Value,
            targetType: "ContentType", targetId: def.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["contentType"] = def.Name,
                ["from"] = before,
                ["to"] = lines?.ContentType ?? "none",
            }, ct: ct);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { Name = def.Name, Lines = def.Lines }, ct);
    }

    private static List<string> Errors(ContentTypeDefinition header, ContentTypeDefinition? child, LinesDefinition asked)
    {
        if (child is null)
        {
            return [$"Line type '{asked.ContentType}' does not exist."];
        }

        if (string.Equals(child.Name, header.Name, StringComparison.OrdinalIgnoreCase))
        {
            return ["A type cannot be its own line type."];
        }

        if (child.Lines is not null)
        {
            return [$"Line type '{child.Name}' has lines of its own. Lines are one level deep."];
        }

        var parent = child.Fields.FirstOrDefault(f => string.Equals(f.Name, asked.ParentField, StringComparison.OrdinalIgnoreCase));
        if (parent is null)
        {
            return [$"Line type '{child.Name}' has no field '{asked.ParentField}'."];
        }

        if (!string.Equals(parent.Type, "reference", StringComparison.OrdinalIgnoreCase)
            || parent.Multiple
            || !string.Equals(parent.ReferenceType, header.Name, StringComparison.OrdinalIgnoreCase))
        {
            return [$"Field '{parent.Name}' of '{child.Name}' must be a single reference to '{header.Name}'."];
        }

        return [];
    }
}
