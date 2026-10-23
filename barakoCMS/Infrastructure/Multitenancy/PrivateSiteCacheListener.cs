using barakoCMS.Features.Site.ShareLinks;
using barakoCMS.Models;
using Marten;
using Marten.Services;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Clears a tenant's <see cref="PrivateSiteCache"/> entry when a commit writes a <c>site</c> entry.
/// </summary>
/// <remarks>
/// On the store, so every write path is covered: the endpoints, config apply, seed files, the
/// scheduler and erasure all commit through a session. A deletion is cleared whatever it deleted of
/// content, since a bulk delete carries no document to inspect. Runs after the commit, and a failure
/// here must not report a committed write as failed.
/// </remarks>
internal sealed class PrivateSiteCacheListener(PrivateSiteCache cache) : DocumentSessionListenerBase
{
    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            if (TouchesSite(commit))
            {
                cache.Invalidate(TenantScopes.SlugFor(session.TenantId));
            }
        }
        catch
        {
            cache.Invalidate(TenantScopes.SlugFor(session.TenantId));
        }

        return Task.CompletedTask;
    }

    internal static bool TouchesSite(IChangeSet commit) =>
        commit.Inserted.Concat(commit.Updated).OfType<Content>().Any(IsSite)
        || commit.Deleted.Any(d => d.Document is Content c ? IsSite(c) : d.DocumentType == typeof(Content));

    private static bool IsSite(Content c) =>
        string.Equals(c.ContentType, ShareLinkKeys.SiteType, StringComparison.OrdinalIgnoreCase);
}
