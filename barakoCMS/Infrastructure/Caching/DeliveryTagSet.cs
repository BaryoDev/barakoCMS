using barakoCMS.Models;
using Marten;
using Marten.Services;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// The delivery cache tags a commit changes. Eviction and CDN purge (#561) both call this, so the
/// two can never name different tags for the same commit.
/// </summary>
/// <remarks>
/// Entry, type, sitemap and untyped-reference tags for each content document; the type tag and the
/// sitemap tag for a type definition; the redirects tag for a redirect; the same for content whose
/// stream the commit wrote events to; and the bare tenant tag when a deletion names no document.
/// </remarks>
internal static class DeliveryTagSet
{
    /// <summary>
    /// The tags for the commit, or the bare tenant tag when working them out failed, which empties the
    /// whole tenant: correct over cheap, once, and rare. <paramref name="onFailure"/> says why.
    /// </summary>
    public static async Task<IReadOnlyCollection<string>> ForCommitAsync(
        IDocumentSession session, IChangeSet commit, string tenant, Action<Exception> onFailure, CancellationToken token)
    {
        try
        {
            return await ComputeAsync(session, commit, tenant, token);
        }
        catch (Exception ex)
        {
            onFailure(ex);
            return [DeliveryCache.TenantTag(tenant)];
        }
    }

    private static async Task<IReadOnlyCollection<string>> ComputeAsync(
        IDocumentSession session, IChangeSet commit, string tenant, CancellationToken token)
    {
        var scopes = new List<CacheScope>();

        var contents = commit.Inserted.Concat(commit.Updated).OfType<ContentDoc>().ToList();
        foreach (var content in contents)
        {
            AddContent(scopes, content.Id, content.ContentType);
        }

        foreach (var definition in commit.Inserted.Concat(commit.Updated).OfType<ContentTypeDefinition>())
        {
            scopes.Add(CacheScope.Type(definition.Name));
            scopes.Add(CacheScope.Named("sitemap"));
        }

        var redirectChanged = commit.Inserted.Concat(commit.Updated).OfType<UrlRedirect>().Any()
            || commit.Deleted.Any(d => d.DocumentType == typeof(UrlRedirect));
        if (redirectChanged)
        {
            scopes.Add(CacheScope.Named("redirects"));
        }

        var known = contents.Select(c => c.Id).ToHashSet();
        foreach (var stream in commit.GetEvents().Select(e => e.StreamId).Distinct().Where(id => !known.Contains(id)))
        {
            var content = await session.LoadAsync<ContentDoc>(stream, token);
            if (content is not null)
            {
                AddContent(scopes, content.Id, content.ContentType);
            }
        }

        foreach (var deleted in commit.Deleted)
        {
            if (deleted.Document is ContentDoc gone)
            {
                AddContent(scopes, gone.Id, gone.ContentType);
            }
            else if (deleted.Document is ContentTypeDefinition type)
            {
                scopes.Add(CacheScope.Type(type.Name));
            }
            else if (deleted.DocumentType == typeof(ContentDoc) || deleted.DocumentType == typeof(ContentTypeDefinition))
            {
                return [DeliveryCache.TenantTag(tenant)];
            }
        }

        return scopes.Select(s => DeliveryCache.Tag(tenant, s)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddContent(List<CacheScope> scopes, Guid id, string type)
    {
        scopes.Add(CacheScope.Entry(id));
        scopes.Add(CacheScope.Type(type));
        scopes.Add(CacheScope.Named("sitemap"));
        scopes.Add(CacheScope.Named(DeliveryResponseCacheListener.UntypedReferences));
    }
}
