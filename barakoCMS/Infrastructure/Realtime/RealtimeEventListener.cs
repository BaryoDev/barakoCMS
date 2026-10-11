using barakoCMS.Infrastructure.Multitenancy;
using Marten;
using Marten.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>What a subscriber hears. Enough to know it should read the feed, nothing more.</summary>
public sealed record RealtimeNotice(string Topic, long Sequence, Guid StreamId, string Type);

/// <summary>
/// Sends a notice to each topic group a committed event belongs to, on the instance that committed it.
/// </summary>
/// <remarks>
/// After commit, so a notice never announces a write that rolled back. Same limit as
/// <c>ContentChangeListener</c>: only connections on the writing instance hear it. A clinic server is
/// one instance; a client that reconnects reads the feed from its last sequence either way, so a
/// missed notice delays a refresh and loses nothing.
///
/// Nothing may escape. This runs inside the caller's SaveChangesAsync after the commit, and a failure
/// here would report a write that succeeded as one that did not.
/// </remarks>
internal sealed class RealtimeEventListener(
    IHubContext<RealtimeHub> hub,
    TopicRegistry topics,
    ILogger<RealtimeEventListener> logger) : DocumentSessionListenerBase
{
    public const string Method = "changed";

    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        try
        {
            foreach (var e in commit.GetEvents())
            {
                var matching = topics.TopicsFor(e.EventType);
                if (matching.Count == 0) continue;

                var slug = TenantScopes.SlugFor(e.TenantId);
                foreach (var topic in matching)
                {
                    await hub.Clients.Group(RealtimeHub.GroupName(slug, topic.Name)).SendAsync(
                        Method,
                        new RealtimeNotice(topic.Name, e.Sequence, e.StreamId, e.EventTypeName),
                        token);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Realtime notice failed after a commit; the write itself succeeded");
        }
    }
}
