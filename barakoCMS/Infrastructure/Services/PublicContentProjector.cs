using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// <see cref="IPublicContentProjector"/> over the projection the core delivery routes use.
/// </summary>
/// <remarks>
/// Every member forwards to <c>PublicDelivery</c>. That is the whole design: the four checks live in
/// one function that <c>/api/public/{type}</c>, <c>/api/public/{type}/{slug}</c>, search and include
/// resolution all already go through, and this adds a module-facing name for it rather than a second
/// copy. The only work done here is renaming the result into a type a module can reference, since the
/// response records under <c>Features/</c> stay internal (CLAUDE.md section 6).
/// </remarks>
internal sealed class PublicContentProjector : IPublicContentProjector
{
    public bool IsDeliverable(ContentTypeDefinition? definition) =>
        PublicDelivery.IsDeliverable(definition);

    public string? SlugField(ContentTypeDefinition? definition) =>
        definition is null ? null : PublicDelivery.SlugField(definition);

    public PublicContentProjection? Project(Content content, ContentTypeDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(content);

        /* The pair has to match, and a mismatch is refused rather than trusted. The schema is what
         * names the Public fields, so another type's definition applies another type's allowlist, and
         * the result still reads as authentic because ContentType comes off the entry. The core routes
         * cannot get here: each resolves the definition by the route's type and queries content by the
         * same type, which held the invariant by construction. This interface hands the pairing to a
         * caller, so the invariant needs stating. */
        if (definition is null
            || !string.Equals(content.ContentType, definition.Name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projected = PublicDelivery.ToPublic(content, definition, PublicDelivery.SlugField(definition));
        if (projected is null)
        {
            return null;
        }

        return new PublicContentProjection(
            projected.Id,
            projected.ContentType,
            projected.Slug,
            projected.Data,
            projected.CreatedAt,
            projected.UpdatedAt,
            projected.Seo is null
                ? null
                : new PublicSeoMetadata(
                    projected.Seo.Title,
                    projected.Seo.Description,
                    projected.Seo.CanonicalUrl,
                    projected.Seo.ImageUrl,
                    projected.Seo.NoIndex));
    }
}
