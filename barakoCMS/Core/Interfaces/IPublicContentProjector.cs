using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The SEO metadata delivery resolves for an entry, with the fallbacks already applied.
/// </summary>
/// <param name="Title">What a search result and a browser tab should say.</param>
/// <param name="Description">The snippet under the title.</param>
/// <param name="CanonicalUrl">The one URL this content should be indexed under, or null.</param>
/// <param name="ImageUrl">The social sharing image, or null.</param>
/// <param name="NoIndex">Whether search engines should be asked to skip this entry.</param>
public sealed record PublicSeoMetadata(
    string? Title,
    string? Description,
    string? CanonicalUrl,
    string? ImageUrl,
    bool NoIndex);

/// <summary>
/// One entry as anonymous delivery serves it: the same JSON shape as
/// <c>GET /api/public/{type}/{slug}</c>.
/// </summary>
/// <remarks>
/// A module serialising this produces what a frontend already reads from the core route, so a
/// client site can point at a module's route without a second parser.
/// </remarks>
/// <param name="Id">The entry's id.</param>
/// <param name="ContentType">The name of the entry's content type.</param>
/// <param name="Slug">The entry's slug, or null when the type is not slug-addressable.</param>
/// <param name="Data">Only the fields the content type marks Public.</param>
/// <param name="CreatedAt">When the entry was created.</param>
/// <param name="UpdatedAt">When the entry was last updated.</param>
/// <param name="Seo">The resolved SEO metadata, or null when the content type has not opted in.</param>
public sealed record PublicContentProjection(
    Guid Id,
    string ContentType,
    string? Slug,
    IReadOnlyDictionary<string, object> Data,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PublicSeoMetadata? Seo = null);

/// <summary>
/// Projects an entry to what anonymous callers may see, or to null when it must not be served.
/// </summary>
/// <remarks>
/// Four checks stand between a stored document and a public response: it has to be Published, the
/// document itself has to be Sensitivity.Public, its content type has to have opted into public
/// delivery, and only fields the type marks Public survive into the payload. A module serving its
/// own public route has to apply all four, and re-implementing them is the obvious way to get it
/// wrong: each one fails open in a different direction, and a copy that drifts looks right until it
/// delivers a draft.
///
/// So the core exposes the projection instead, and the core routes under <c>/api/public</c> use the
/// same code. There is one copy of the four checks and a module does not hold any of it.
///
/// Nothing here queries. The caller loads the entry and its definition from its own session, which
/// keeps tenant scoping where it already is.
/// </remarks>
public interface IPublicContentProjector
{
    /// <summary>
    /// Whether anonymous delivery may serve this content type at all.
    /// </summary>
    /// <remarks>
    /// Answer a caller with 404 when this is false, the same as for a type that does not exist.
    /// Answering differently would confirm which types exist. Null counts as not deliverable, so an
    /// unfound definition can be passed straight in.
    /// </remarks>
    bool IsDeliverable(ContentTypeDefinition? definition);

    /// <summary>
    /// The field holding an entry's slug, or null when the type is not slug-addressable.
    /// </summary>
    /// <remarks>
    /// A field of type "slug", else a field named "slug", case-insensitively. Exposed because a
    /// module addressing entries by slug has to query the same field delivery reads back. Null in
    /// gives null out, so the result of a FirstOrDefaultAsync can be passed straight in, the same as
    /// for the other two members.
    /// </remarks>
    string? SlugField(ContentTypeDefinition? definition);

    /// <summary>
    /// The entry as it may be served, or null when it may not be served at all.
    /// </summary>
    /// <remarks>
    /// The definition has to be the entry's own content type, and a pair that does not match projects
    /// to null. It is the schema that says which fields are Public, so another type's schema applies
    /// another type's allowlist, and an entry whose real type marks a field Sensitive would be served
    /// with that field in it.
    /// </remarks>
    /// <param name="content">The document, loaded by the caller.</param>
    /// <param name="definition">
    /// Its content type, matched by name case-insensitively. Null projects to null: with no schema
    /// saying which fields are Public, nothing is delivered.
    /// </param>
    PublicContentProjection? Project(Content content, ContentTypeDefinition? definition);
}
