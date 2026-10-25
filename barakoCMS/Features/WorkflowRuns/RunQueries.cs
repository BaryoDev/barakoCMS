using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// Reads over many runs at once. All of them go through Marten's LINQ on the caller's query session,
/// so the tenant filter is Marten's and not something written into a statement here.
/// </summary>
internal static class RunQueries
{
    /// <summary>The run count of every status, zero where there is none, in one grouped query.</summary>
    internal static async Task<Dictionary<string, int>> CountByStatusAsync(
        IQuerySession session, Guid? contentId, Guid? workflowId, CancellationToken ct)
    {
        var q = session.Query<WorkflowRun>().AsQueryable();
        if (contentId is { } c) q = q.Where(r => r.ContentId == c);
        if (workflowId is { } w) q = q.Where(r => r.WorkflowDefinitionId == w);

        var groups = await q
            .GroupBy(r => r.Status)
            .Select(g => new StatusCount { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var counts = Enum.GetValues<RunStatus>().ToDictionary(each => each.ToString(), _ => 0);
        foreach (var group in groups) counts[group.Status.ToString()] = group.Count;
        return counts;
    }

    /// <summary>
    /// Per workflow, the runs of one status created at or after a time: one grouped query per status
    /// asked for, however many workflows are on the page.
    /// </summary>
    internal static async Task<Dictionary<(Guid Workflow, RunStatus Status), int>> CountSinceAsync(
        IQuerySession session, IReadOnlyCollection<Guid> workflowIds, DateTimeOffset from,
        IEnumerable<RunStatus> statuses, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, RunStatus), int>();
        if (workflowIds.Count == 0) return result;

        var ids = workflowIds.ToList();
        foreach (var status in statuses)
        {
            var wanted = status;
            var groups = await session.Query<WorkflowRun>()
                .Where(r => ids.Contains(r.WorkflowDefinitionId) && r.Status == wanted && r.CreatedAt >= from)
                .GroupBy(r => r.WorkflowDefinitionId)
                .Select(g => new WorkflowCount { Workflow = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            foreach (var group in groups) result[(group.Workflow, wanted)] = group.Count;
        }

        return result;
    }

    /// <summary>
    /// For the caller's tenant, the runs of each status created at or after a time, per workflow:
    /// one grouped query per status asked for.
    /// </summary>
    internal static async Task<Dictionary<(Guid Workflow, RunStatus Status), int>> CountSinceByWorkflowAsync(
        IQuerySession session, DateTimeOffset from, IEnumerable<RunStatus> statuses, CancellationToken ct)
    {
        var result = new Dictionary<(Guid, RunStatus), int>();
        foreach (var status in statuses)
        {
            var wanted = status;
            var groups = await session.Query<WorkflowRun>()
                .Where(r => r.Status == wanted && r.CreatedAt >= from)
                .GroupBy(r => r.WorkflowDefinitionId)
                .Select(g => new WorkflowCount { Workflow = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            foreach (var group in groups) result[(group.Workflow, wanted)] = group.Count;
        }

        return result;
    }

    /// <summary>The caller's newest runs whatever their status, newest first, off the CreatedAt index.</summary>
    internal static async Task<IReadOnlyList<WorkflowRun>> NewestAsync(
        IQuerySession session, int count, CancellationToken ct) =>
        await session.Query<WorkflowRun>()
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .Take(count)
            .ToListAsync(ct);

    /// <summary>
    /// The mean recorded duration of the actions of a workflow's newest runs created at or after a
    /// time, or null when none of them recorded one.
    /// </summary>
    internal static async Task<double?> AverageDurationAsync(
        IQuerySession session, Guid workflowId, DateTimeOffset from, int sample, CancellationToken ct)
    {
        var sampled = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId && r.CreatedAt >= from)
            .OrderByDescending(r => r.CreatedAt)
            .Take(sample)
            .Select(r => new RunActions { Actions = r.Actions })
            .ToListAsync(ct);

        var durations = sampled
            .SelectMany(r => r.Actions)
            .Where(a => a.DurationMs is not null)
            .Select(a => a.DurationMs!.Value)
            .ToList();

        return durations.Count == 0 ? null : durations.Average();
    }

    /// <summary>A workflow's newest finished runs (not pending, not running), newest first.</summary>
    internal static async Task<IReadOnlyList<WorkflowRun>> RecentFinishedAsync(
        IQuerySession session, Guid workflowId, int count, CancellationToken ct) =>
        await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId
                        && r.Status != RunStatus.Pending && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.CreatedAt)
            .Take(count)
            .ToListAsync(ct);

    private sealed class StatusCount
    {
        public RunStatus Status { get; set; }
        public int Count { get; set; }
    }

    private sealed class WorkflowCount
    {
        public Guid Workflow { get; set; }
        public int Count { get; set; }
    }

    private sealed class RunActions
    {
        public List<WorkflowActionAttempt> Actions { get; set; } = new();
    }
}
