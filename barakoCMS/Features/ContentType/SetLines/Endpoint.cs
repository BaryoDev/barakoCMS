using barakoCMS.Core.Validation;
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
        // The type's lock, so a rollup added to it meanwhile is seen below.
        await barakoCMS.Infrastructure.Jobs.FieldChanges.LockTypeAsync(session, name, ct);
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

            var errors = LinesRules.Errors(def, child, asked);
            foreach (var error in errors)
            {
                AddError(error);
            }

            ThrowIfAnyErrors();
            lines = LinesRules.Stored(def, child!, asked);
        }

        var before = def.Lines?.ContentType ?? "none";
        def.Lines = lines;
        def.UpdatedAt = DateTimeOffset.UtcNow;
        session.Store(def);

        var actorId = ActingCaller.IdOf(User);
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
}
