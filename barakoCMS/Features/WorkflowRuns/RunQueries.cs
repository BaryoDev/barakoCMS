using barakoCMS.Models;
using Marten;
using barakoCMS.Features.Content.Counts;

namespace barakoCMS.Features.WorkflowRuns;

/// <summary>
/// Reads over many runs at once. All of them go through Marten's LINQ on the caller's query session,
/// so the tenant filter is Marten's and not something written into a statement here.
/// </summary>
internal static class RunQueries
{
    /// <summary>The most runs counted for one status on a list read. A count that reaches it reads "this many or more".</summary>
    internal const int StatusCountCap = 1000;

    /// <summary>
    /// The run count of every status, zero where there is none, counted only up to a cap for each status.
    /// </summary>
    /// <remarks>
    /// A grouped count over every run of the tenant costs as much as the table is long, and the list asks
    /// on each read. This reads at most <paramref name="cap"/> + 1 ids per status, so the cost has a ceiling
    /// however long the history grows. A status that has more than the cap reports the cap and is named in
    /// <see cref="StatusCounts.Capped"/>.
    /// </remarks>
    internal static async Task<StatusCounts> CountByStatusAsync(
        IQuerySession session, Guid? contentId, Guid? workflowId, CancellationToken ct, int cap = StatusCountCap)
    {
        var counts = new Dictionary<string, int>();
        var capped = new List<string>();

        foreach (var status in Enum.GetValues<RunStatus>())
        {
            var wanted = status;
            var q = session.Query<WorkflowRun>().Where(r => r.Status == wanted);
            if (contentId is { } c) q = q.Where(r => r.ContentId == c);
            if (workflowId is { } w) q = q.Where(r => r.WorkflowDefinitionId == w);

            var ids = await q.Select(r => r.Id).Take(cap + 1).ToListAsync(ct);
            counts[status.ToString()] = Math.Min(ids.Count, cap);
            if (ids.Count > cap) capped.Add(status.ToString());
        }

        return new StatusCounts(counts, cap, capped);
    }

    /// <summary>Counts by status, the cap each was counted to, and the statuses that reached past it.</summary>
    internal sealed record StatusCounts(Dictionary<string, int> Counts, int Cap, List<string> Capped);

    /// <summary>A count for every status, zero where there is none, from a count by status.</summary>
    internal static Dictionary<string, int> ByStatus(Func<RunStatus, int> of) =>
        Enum.GetValues<RunStatus>().ToDictionary(each => each.ToString(), of);

    /// <summary>
    /// The runs created in a window and in the one before it, by status and bucket, under the same
    /// content and workflow filters as <see cref="CountByStatusAsync"/>: one query.
    /// </summary>
    internal static Task<WindowCells> CountWindowAsync(
        IQuerySession session, Guid? contentId, Guid? workflowId, CountWindow window, CancellationToken ct)
    {
        var floor = window.PreviousFrom;
        var ceiling = window.To;
        var q = session.Query<WorkflowRun>().Where(r => r.CreatedAt >= floor && r.CreatedAt < ceiling);
        if (contentId is { } c) q = q.Where(r => r.ContentId == c);
        if (workflowId is { } w) q = q.Where(r => r.WorkflowDefinitionId == w);

        return CountWindow.TallyAsync(session, [q], window, ct);
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
