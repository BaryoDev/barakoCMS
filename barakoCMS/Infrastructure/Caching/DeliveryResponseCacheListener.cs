using barakoCMS.Infrastructure.Multitenancy;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;

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
    /// <summary>The tag on a read that holds a reference field with no target type.</summary>
    public const string UntypedReferences = "untyped-refs";

    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var tenant = TenantScopes.SlugFor(session.TenantId);
        var tags = await DeliveryTagSet.ForCommitAsync(session, commit, tenant,
            ex => logger.LogError(ex, "Working out the delivery response cache tags failed after a commit; the whole tenant is evicted"),
            token);

        // Inside a content batch this is a flush, not a commit. A read before the commit would cache
        // the old state again, so the eviction waits for the commit.
        Task Evict(CancellationToken _) => EvictAsync(tags, tenant);
        if (!BatchTransaction.TryDeferUntilCommit(Evict))
        {
            await Evict(token);
        }
    }

    private async Task EvictAsync(IReadOnlyCollection<string> tags, string tenant)
    {
        try
        {
            foreach (var tag in tags)
            {
                await store.EvictByTagAsync(tag, CancellationToken.None);
            }
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
}
