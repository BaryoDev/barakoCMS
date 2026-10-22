using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Infrastructure.Caching;

/// <summary>
/// Evicts the cached delivery responses a commit changes, by the same tags the reads were sent with (#562).
/// </summary>
/// <remarks>
/// On the store, so every write path is covered: the endpoints, the scheduler, config apply, seed
/// files and erasure all commit through a session. It runs after the commit on the writing instance,
/// before the write call returns, so a publish is visible to the next read on that instance. Any
/// content change evicts the entry's tag, its type's tag and the sitemap tag, which covers a publish,
/// an unpublish, an edit and a sensitivity change alike. A type definition change evicts the type's
/// tag. A deletion with no document to inspect evicts the whole tenant. A failure here must not
/// report a committed write as failed, and it falls back to evicting the whole tenant.
/// </remarks>
internal sealed class DeliveryResponseCacheListener(
    IOutputCacheStore store,
    ILogger<DeliveryResponseCacheListener> logger) : DocumentSessionListenerBase
{
    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var tenant = TenantScopes.SlugFor(session.TenantId);
        try
        {
            var tags = await TagsAsync(session, commit, tenant, token);
            foreach (var tag in tags)
                await store.EvictByTagAsync(tag, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Evicting the delivery response cache failed after a commit; the whole tenant is evicted");
            try
            {
                await store.EvictByTagAsync(DeliveryCache.TenantTag(tenant), CancellationToken.None);
            }
            catch (Exception inner)
            {
                logger.LogError(inner, "Evicting the whole tenant from the delivery response cache failed");
            }
        }
    }

    private static async Task<IReadOnlyCollection<string>> TagsAsync(
        IDocumentSession session, IChangeSet commit, string tenant, CancellationToken token)
    {
        var scopes = new List<CacheScope>();

        var contents = commit.Inserted.Concat(commit.Updated).OfType<ContentDoc>().ToList();
        foreach (var content in contents)
            AddContent(scopes, content.Id, content.ContentType);

        foreach (var definition in commit.Inserted.Concat(commit.Updated).OfType<ContentTypeDefinition>())
        {
            scopes.Add(CacheScope.Type(definition.Name));
            scopes.Add(CacheScope.Named("sitemap"));
        }

        var known = contents.Select(c => c.Id).ToHashSet();
        foreach (var stream in commit.GetEvents().Select(e => e.StreamId).Distinct().Where(id => !known.Contains(id)))
        {
            var content = await session.LoadAsync<ContentDoc>(stream, token);
            if (content is not null)
                AddContent(scopes, content.Id, content.ContentType);
        }

        foreach (var deleted in commit.Deleted)
        {
            if (deleted.Document is ContentDoc gone)
                AddContent(scopes, gone.Id, gone.ContentType);
            else if (deleted.Document is ContentTypeDefinition type)
                scopes.Add(CacheScope.Type(type.Name));
            else if (deleted.DocumentType == typeof(ContentDoc) || deleted.DocumentType == typeof(ContentTypeDefinition))
                return [DeliveryCache.TenantTag(tenant)];
        }

        return scopes.Select(s => DeliveryCache.Tag(tenant, s)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddContent(List<CacheScope> scopes, Guid id, string type)
    {
        scopes.Add(CacheScope.Entry(id));
        scopes.Add(CacheScope.Type(type));
        scopes.Add(CacheScope.Named("sitemap"));
    }
}
