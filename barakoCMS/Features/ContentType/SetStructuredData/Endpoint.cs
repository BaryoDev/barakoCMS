using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetStructuredData;

/// <summary>
/// PUT /api/content-types/{name}/structured-data, which sets or clears the schema.org type a
/// single delivered entry of the type is described as.
/// </summary>
/// <remarks>
/// Its own endpoint for the reason the route template has one: content types have no general
/// update. No entry is read or written. The block is built when an entry is read by slug, and the
/// type's <c>updatedAt</c> moves, so a cached read revalidates.
/// </remarks>
internal class Endpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Services.IContentTypeValidatorService validator,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Put("/api/content-types/{name}/structured-data");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(Route<string>("name") ?? string.Empty);

        var def = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        if (def is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (valid, errors) = validator.ValidateStructuredDataType(req.StructuredDataType);
        if (!valid)
        {
            foreach (var error in errors) AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!string.Equals(def.StructuredDataType, req.StructuredDataType, StringComparison.Ordinal))
        {
            var before = def.StructuredDataType ?? "none";

            def.StructuredDataType = req.StructuredDataType;
            def.UpdatedAt = DateTimeOffset.UtcNow;
            session.Store(def);

            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(
                session,
                tenant.Slug,
                "contenttype.structureddata.changed",
                actorId,
                User.FindFirst("Username")?.Value,
                targetType: "ContentType",
                targetId: def.Id.ToString(),
                metadata: new Dictionary<string, object>
                {
                    ["contentType"] = def.Name,
                    ["from"] = before,
                    ["to"] = def.StructuredDataType ?? "none",
                },
                ct: ct);

            await session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new Response { Name = def.Name, StructuredDataType = def.StructuredDataType }, ct);
    }
}
