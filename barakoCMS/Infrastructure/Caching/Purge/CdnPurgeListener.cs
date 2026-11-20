using barakoCMS.Infrastructure.Multitenancy;
using Marten;
using Marten.Services;

namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// After a commit that changes what public delivery serves, writes a pending CDN purge for the tags
/// that commit touched (#561).
/// </summary>
/// <remarks>
/// On the store, so every write path is covered, and registered whether or not the response cache is
/// on. The tags come from <see cref="DeliveryTagSet"/>, the same function the eviction listener uses.
/// The call to the provider is never made here: the row is written after the commit and the sender
/// delivers it. Inside a content batch the tags are gathered and one row is written when the batch
/// commits, and a batch that rolls back writes nothing. A failure here is logged and never fails the
/// write that committed.
/// </remarks>
internal sealed class CdnPurgeListener(
    CdnPurgeGate gate,
    CdnPurgeSignal signal,
    IConfiguration configuration,
    ILogger<CdnPurgeListener> logger) : DocumentSessionListenerBase
{
    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            foreach (var settings in commit.Inserted.Concat(commit.Updated).OfType<CdnPurgeSettings>())
            {
                gate.Forget(settings.TenantSlug);
            }

            var tenantId = session.TenantId;
            var tenant = TenantScopes.SlugFor(tenantId);
            var store = session.DocumentStore;
            if (!await gate.IsOnAsync(store, tenant, token))
            {
                return;
            }

            var tags = await DeliveryTagSet.ForCommitAsync(session, commit, tenant,
                ex => logger.LogError(ex, "Working out the CDN purge tags failed after a commit; the whole tenant is purged"),
                token);
            if (tags.Count == 0)
            {
                return;
            }

            if (BatchTransaction.TryHold(this, tenantId, () => new Held(store, tenantId, tenant), WriteHeldAsync, out var held))
            {
                held.Tags.UnionWith(tags);
                return;
            }

            await WriteAsync(store, tenantId, tenant, tags);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Queueing a CDN purge failed after a commit; the write itself committed");
        }
    }

    private sealed record Held(IDocumentStore Store, string TenantId, string Tenant)
    {
        public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
    }

    private Task WriteHeldAsync(Held held, CancellationToken _) =>
        WriteAsync(held.Store, held.TenantId, held.Tenant, held.Tags);

    private async Task WriteAsync(IDocumentStore store, string tenantId, string tenant, IEnumerable<string> tags)
    {
        try
        {
            var max = CdnPurgeOptions.From(configuration).MaxTagsPerFlush;
            var bounded = CdnPurgeBounds.Collapse(tags, tenant, max, out _);
            await using var write = store.LightweightSession(tenantId);
            write.Store(new PendingCdnPurge { Id = Guid.NewGuid(), Tags = bounded.ToList(), WrittenAt = DateTimeOffset.UtcNow });
            // The commit is already done, so a request that disconnects now must not drop the purge.
            await write.SaveChangesAsync(CancellationToken.None);
            signal.Nudge();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Writing a pending CDN purge failed after a commit; the write itself committed");
        }
    }
}
