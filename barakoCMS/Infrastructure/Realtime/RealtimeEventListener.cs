using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
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
/// <c>ContentChangeListener</c>: only connections on the writing instance hear it. A single-node server is
/// one instance; a client that reconnects reads the feed from its last sequence either way, so a
/// missed notice delays a refresh and loses nothing.
///
/// Nothing may escape. This runs inside the caller's SaveChangesAsync after the commit, and a failure
/// here would report a write that succeeded as one that did not.
///
/// A content topic notice goes to one group. A branch scoped type sends it to the group of the
/// entry's branch, so a reader of another branch never hears it; an entry with no branch is sent
/// to no one. The entry row is read after the commit for its type and branch, and the notice holds
/// ids only.
/// </remarks>
internal sealed class RealtimeEventListener(
    IHubContext<RealtimeHub> hub,
    TopicRegistry topics,
    ContentTopicCatalog content,
    Func<IDocumentStore> store,
    ILogger<RealtimeEventListener> logger) : DocumentSessionListenerBase
{
    public const string Method = "changed";

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var events = commit.GetEvents().ToList();
        var runs = commit.Inserted.Concat(commit.Updated).OfType<WorkflowRun>().Select(r => r.Id).Distinct().ToList();
        var tenantId = session.TenantId;

        // Inside a content batch this is a flush, not a commit. The notices go out once it commits.
        return BatchTransaction.TryDeferUntilCommit(ct => NotifyAsync(events, runs, tenantId, ct))
            ? Task.CompletedTask
            : NotifyAsync(events, runs, tenantId, token);
    }

    private async Task NotifyAsync(
        IReadOnlyList<JasperFx.Events.IEvent> events,
        IReadOnlyList<Guid> runs,
        string tenantId,
        CancellationToken token)
    {
        try
        {
            // The group is the session's tenant, so a run written under tenant B reaches only B's subscribers.
            var runGroup = RealtimeHub.GroupName(TenantScopes.SlugFor(tenantId), WorkflowRunTopic.Name);
            foreach (var run in runs)
                await hub.Clients.Group(runGroup).SendAsync(Method, WorkflowRunTopic.Notice(run), token);

            foreach (var e in events)
            {
                var slug = TenantScopes.SlugFor(e.TenantId);

                var matching = topics.TopicsFor(e.EventType);
                if (matching.Count == 0) continue;

                foreach (var topic in matching)
                {
                    await hub.Clients.Group(RealtimeHub.GroupName(slug, topic.Name)).SendAsync(
                        Method,
                        new RealtimeNotice(topic.Name, e.Sequence, e.StreamId, e.EventTypeName),
                        token);
                }
            }

            await NotifyContentAsync(events, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Realtime notice failed after a commit; the write itself succeeded");
        }
    }

    private async Task NotifyContentAsync(IReadOnlyList<JasperFx.Events.IEvent> committed, CancellationToken token)
    {
        var events = committed.Where(e => ContentTopicEvents.Types.Contains(e.EventType)).ToList();
        foreach (var tenantEvents in events.GroupBy(e => e.TenantId))
        {
            var slug = TenantScopes.SlugFor(tenantEvents.Key);
            await using var session = store().QuerySession(tenantEvents.Key);
            var ids = tenantEvents.Select(e => e.StreamId).Distinct().ToArray();
            var rows = await session.LoadManyAsync<Content>(token, ids);
            var byId = rows.ToDictionary(r => r.Id);

            foreach (var e in tenantEvents)
            {
                if (!byId.TryGetValue(e.StreamId, out var row)) continue;
                var topic = await content.ForTypeAsync(slug, row.ContentType, token);
                if (topic is null) continue;

                var group = GroupFor(slug, topic, row);
                if (group is null) continue;

                await hub.Clients.Group(group).SendAsync(
                    Method,
                    new RealtimeNotice(topic.Name, e.Sequence, e.StreamId, e.EventTypeName),
                    token);
            }
        }
    }

    private static string? GroupFor(string slug, ContentTopic topic, Content row)
    {
        if (!topic.BranchScoped) return RealtimeHub.GroupName(slug, topic.Name);
        return row.BranchId is { } branch ? RealtimeHub.BranchGroupName(slug, topic.Name, branch) : null;
    }
}
