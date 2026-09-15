using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Pages;

/// <summary>Loading pages for the anonymous endpoints. Everything returned has been through the projector.</summary>
/// <remarks>
/// The query narrows to Published and document-Public so the database does the bulk of the work, and
/// the projector is still applied to every row: it holds the four delivery checks and the field
/// allowlist, and this module holds none of them. Tree fields (parent, navigation flag, order, title)
/// are read from the projected data, so a field the type marks non-Public shapes nothing here.
/// </remarks>
internal static class PublishedPages
{
    /// <summary>
    /// A case-insensitive match of one data field, the same fragment core's slug route uses. Both
    /// the field name and the value are bound parameters.
    /// </summary>
    private const string FieldEqualsIgnoreCaseSql =
        "lower((SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) #>> '{}') = lower(?)";

    public static Task<ContentTypeDefinition?> DefinitionAsync(IQuerySession session, string contentType, CancellationToken ct)
    {
        var lowered = contentType.ToLower();
        return session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
    }

    /// <summary>
    /// Published pages, oldest first, at most <see cref="PagesOptions.MaxPages"/>, and whether there
    /// were more than that.
    /// </summary>
    public static async Task<(List<(PageNode Node, PublicContentProjection Entry)> Pages, bool Truncated)> AllAsync(
        IQuerySession session,
        IPublicContentProjector projector,
        ContentTypeDefinition definition,
        PagesOptions options,
        CancellationToken ct)
    {
        var name = definition.Name;
        var docs = await session.Query<Content>()
            .Where(c => c.ContentType == name
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(options.MaxPages + 1)
            .ToListAsync(ct);

        return (Project(docs.Take(options.MaxPages), projector, definition, options), docs.Count > options.MaxPages);
    }

    /// <summary>Published pages holding <paramref name="slug"/>, oldest first, at most a handful.</summary>
    public static async Task<List<(PageNode Node, PublicContentProjection Entry)>> WithSlugAsync(
        IQuerySession session,
        IPublicContentProjector projector,
        ContentTypeDefinition definition,
        PagesOptions options,
        string slug,
        CancellationToken ct)
    {
        if (projector.SlugField(definition) is not { } slugField)
        {
            return [];
        }

        var name = definition.Name;
        var docs = await session.Query<Content>()
            .Where(c => c.ContentType == name
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public
                        && c.MatchesSql(FieldEqualsIgnoreCaseSql, slugField, slug))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(10)
            .ToListAsync(ct);

        return Project(docs, projector, definition, options);
    }

    /// <summary>A published page by id, or null when it may not be served.</summary>
    public static async Task<(PageNode Node, PublicContentProjection Entry)?> ByIdAsync(
        IQuerySession session,
        IPublicContentProjector projector,
        ContentTypeDefinition definition,
        PagesOptions options,
        Guid id,
        CancellationToken ct)
    {
        var doc = await session.LoadAsync<Content>(id, ct);
        if (doc is null)
        {
            return null;
        }

        var projected = Project([doc], projector, definition, options);
        return projected.Count == 1 ? projected[0] : null;
    }

    public static void SetCache(HttpContext http)
    {
        http.Response.Headers.CacheControl = "public, max-age=60";
        http.Response.Headers.Vary = barakoCMS.Infrastructure.Multitenancy.TenantResolutionMiddleware.TenantHeader;
    }

    private static List<(PageNode, PublicContentProjection)> Project(
        IEnumerable<Content> docs,
        IPublicContentProjector projector,
        ContentTypeDefinition definition,
        PagesOptions options)
    {
        var result = new List<(PageNode, PublicContentProjection)>();
        foreach (var doc in docs)
        {
            if (projector.Project(doc, definition) is not { } entry)
            {
                continue;
            }

            var node = new PageNode(
                entry.Id,
                entry.Slug,
                PageData.String(entry.Data, options.TitleField),
                PageData.Guid(entry.Data, options.ParentField),
                PageData.Bool(entry.Data, options.ShowInNavigationField),
                PageData.Int(entry.Data, options.OrderField));

            result.Add((node, entry));
        }

        return result;
    }
}
