using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Events;

namespace barakoCMS.Features.Changes;

/// <summary>What a content topic item says about the entry now. Ids and the state, no field values.</summary>
internal sealed record ContentChangeData(string ContentType, Guid Id, string? LifecycleState, Guid? BranchId);

/// <summary>
/// The change feed of a content topic: the content events after a sequence, for the entries of the
/// topic's type that the caller may read.
/// </summary>
/// <remarks>
/// Each item reports the entry as it is now, not as it was at the event, so a client reads the
/// entry again for the rest. The checks are the ones the content GET makes: the read permission on
/// the entry with its rule conditions, the branch of a branch scoped type, and a hidden entry.
/// Events of other types are skipped, so a busy neighbour could keep the loop reading without
/// finding an item. It reads at most <see cref="MaxRawPages"/> pages and returns the sequence it
/// reached, and the client continues from there.
/// </remarks>
internal static class ContentChangeFeed
{
    public const int MaxRawPages = 10;

    public static async Task<(List<ChangeItem> Items, long Next)> ReadAsync(
        ContentTopic topic, ClaimsPrincipal caller, User user, long after, long ceiling, int limit,
        IQuerySession session, IPermissionResolver permissions, IBranchScopeGuard branchScope,
        ISensitivityService sensitivity, HttpContext http, CancellationToken ct)
    {
        var items = new List<ChangeItem>();
        var cursor = after;

        for (var page = 0; page < MaxRawPages && cursor < ceiling && items.Count < limit; page++)
        {
            var from = cursor;
            var raw = await session.Events.QueryAllRawEvents()
                .Where(e => e.Sequence > from && e.Sequence <= ceiling)
                .Where(e => e.EventTypesAre(ContentTopicEvents.Types))
                .OrderBy(e => e.Sequence)
                .Take(limit)
                .ToListAsync(ct);
            if (raw.Count == 0)
            {
                cursor = ceiling;
                break;
            }

            var ids = raw.Select(e => e.StreamId).Distinct().ToArray();
            var rows = (await session.LoadManyAsync<barakoCMS.Models.Content>(ct, ids)).ToDictionary(c => c.Id);
            foreach (var e in raw)
            {
                cursor = e.Sequence;
                if (!rows.TryGetValue(e.StreamId, out var row)) continue;
                if (!await MayReadAsync(topic, row, caller, user, branchScope, permissions, sensitivity, http, ct)) continue;

                items.Add(new ChangeItem
                {
                    Sequence = e.Sequence,
                    StreamId = e.StreamId,
                    Type = e.EventTypeName,
                    Topics = [topic.Name],
                    Timestamp = e.Timestamp,
                    Data = new ContentChangeData(row.ContentType, row.Id, row.LifecycleState, row.BranchId),
                });
                if (items.Count == limit) break;
            }

            // A short page read everything up to the ceiling.
            if (raw.Count < limit && cursor < ceiling && items.Count < limit) cursor = ceiling;
        }

        return (items, cursor);
    }

    private static async Task<bool> MayReadAsync(
        ContentTopic topic, barakoCMS.Models.Content row, ClaimsPrincipal caller, User user, IBranchScopeGuard branchScope,
        IPermissionResolver permissions,
        ISensitivityService sensitivity, HttpContext http, CancellationToken ct)
    {
        if (!string.Equals(row.ContentType, topic.ContentType, StringComparison.OrdinalIgnoreCase)) return false;
        if (!await branchScope.MayAccessAsync(caller, topic.BranchScoped, row.BranchId, ct)) return false;
        if (!await permissions.CanPerformActionAsync(user, row.ContentType, "read", row, ct)) return false;

        // The GET hides the type of a hidden entry from this caller, so the feed leaves it out too.
        return !await sensitivity.ApplyAsync(row, new Dictionary<string, object>(), http, ct);
    }
}
