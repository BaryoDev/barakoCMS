using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Seo;

internal sealed class AddSeoFieldsRequest
{
    public string Name { get; set; } = string.Empty;
}

internal sealed class AddSeoFieldsResponse
{
    public string ContentType { get; init; } = string.Empty;

    /// <summary>Fields this call added.</summary>
    public List<string> Added { get; init; } = new();

    /// <summary>Fields the type already had, left exactly as they were.</summary>
    public List<string> AlreadyPresent { get; init; } = new();
}

/// <summary>
/// POST /api/content-types/{name}/seo-fields. Adds the SEO field set to a content type.
/// </summary>
/// <remarks>
/// An endpoint rather than a checkbox on the type, because opting in is adding fields and this is
/// the one place that has to be true: the fields must arrive through the same shape everything else
/// reads, or delivery, validation and the admin form each need a special case for SEO.
///
/// Additive and idempotent. A field the type already has is reported and left alone rather than
/// overwritten, because a client may well have renamed the display name, made one required, or
/// changed a type, and none of that is this endpoint's to undo.
/// </remarks>
internal sealed class AddSeoFieldsEndpoint : Endpoint<AddSeoFieldsRequest, AddSeoFieldsResponse>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public AddSeoFieldsEndpoint(
        IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/content-types/{name}/seo-fields");
        Roles("SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(AddSeoFieldsRequest req, CancellationToken ct)
    {
        var name = barakoCMS.Core.ContentTypeName.Normalize(req.Name);

        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == name, ct);

        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var added = new List<string>();
        var present = new List<string>();

        foreach (var field in SeoFields.Definitions())
        {
            var existing = definition.Fields
                .FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                present.Add(existing.Name);
                continue;
            }

            definition.Fields.Add(field);
            added.Add(field.Name);
        }

        if (added.Count > 0)
        {
            definition.UpdatedAt = DateTime.UtcNow;
            _session.Store(definition);

            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(_session, _tenant.Slug, "contenttype.seo_fields_added", actorId,
                User.FindFirst("Username")?.Value,
                targetType: nameof(ContentTypeDefinition), targetId: definition.Name,
                metadata: new Dictionary<string, object> { ["added"] = string.Join(", ", added) }, ct: ct);

            await _session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new AddSeoFieldsResponse
        {
            ContentType = definition.Name,
            Added = added,
            AlreadyPresent = present,
        }, ct);
    }
}
