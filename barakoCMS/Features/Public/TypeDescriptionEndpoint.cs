using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Public;

/// <param name="Name">The field's name, the key it is delivered under.</param>
/// <param name="Type">The field type, as the content type declares it.</param>
/// <param name="Role">What the field is to the entry (title, summary, date and so on), or null.</param>
/// <param name="Editor">The editor hint the field declares, or null.</param>
internal sealed record PublicFieldDescription(string Name, string Type, string? Role, string? Editor);

/// <param name="Name">The content type's name, as delivery routes take it.</param>
/// <param name="RouteTemplate">
/// Where an entry of this type lives on the site, such as <c>/blog/{slug}</c>, or null when the
/// type declares none. A renderer falls back to its own path then, as the feed and the sitemap do.
/// </param>
/// <param name="Fields">The type's Public fields, in declared order.</param>
/// <param name="StructuredDataType">
/// The schema.org type a read by slug describes an entry as, or null when the type emits none.
/// </param>
internal sealed record PublicTypeDescription(
    string Name,
    string? RouteTemplate,
    IReadOnlyList<PublicFieldDescription> Fields,
    string? StructuredDataType = null);

/// <summary>
/// GET /api/public/types/{type}/description, what a renderer needs to build titles, links, a
/// sitemap and a feed from a publicly deliverable type's own declaration: its route template, and
/// the name, type, role and editor hint of each Public field.
/// </summary>
/// <remarks>
/// <para>
/// A field is listed exactly when <see cref="PublicDelivery.ToPublic"/> would deliver its value,
/// by the same <see cref="PublicDelivery.IsDeliveredField"/>. Those names are already in every
/// delivered entry, so this widens nothing. A Sensitive or Hidden field is not named, and neither its sensitivity, its rules, its
/// default nor its visible roles are returned for any field.
/// </para>
/// <para>
/// A type that is not publicly deliverable answers 404, the same as an unknown one, so the route
/// cannot confirm which types exist.
/// </para>
/// <para>
/// Three segments under <c>/api/public/</c> on purpose. A content type name may be any text, so a
/// literal in the first or second segment would be a type name too: <c>/api/public/types/{type}</c>
/// takes <c>/api/public/types/{slug}</c> from a type named <c>types</c>. Every route a content type
/// is read on, <c>{type}</c>, <c>{type}/search</c>, <c>{type}/semantic</c>, <c>{type}/feed.xml</c>
/// and <c>{type}/{slug}</c>, is at most two, so this route shadows none of them.
/// </para>
/// </remarks>
internal sealed class TypeDescriptionEndpoint(IQuerySession session) : EndpointWithoutRequest<PublicTypeDescription>
{
    public override void Configure()
    {
        Get("/api/public/types/{type}/description");
        AllowAnonymous();
        Options(x => x
            .RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.DeliveryPolicy)
            .WithMetadata(barakoCMS.Infrastructure.Caching.DeliveryCache.Validators));
        Description(b => b
            .Produces<PublicTypeDescription>(200)
            .Produces(404));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        if (!PublicDelivery.IsDeliverable(def)) { await Send.NotFoundAsync(ct); return; }

        var fields = def!.Fields
            .Where(PublicDelivery.IsDeliveredField)
            .Select(f => new PublicFieldDescription(f.Name, f.Type, f.Role, f.Editor))
            .ToList();

        var template = barakoCMS.Core.Validation.FieldPresentation.IsRouteTemplate(def.RouteTemplate)
            ? def.RouteTemplate
            : null;

        PublicDelivery.SetCache(HttpContext, [barakoCMS.Infrastructure.Caching.CacheScope.Type(def.Name)], def.UpdatedAt);
        var structured = barakoCMS.Core.Validation.StructuredDataTypes.IsKnown(def.StructuredDataType)
            ? def.StructuredDataType
            : null;

        await Send.OkAsync(new PublicTypeDescription(def.Name, template, fields, structured), ct);
    }
}
