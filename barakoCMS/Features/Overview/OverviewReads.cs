using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Overview;

/// <summary>
/// The reads behind a needs-you item that the overview route and <see cref="NeedsYouSource"/> both
/// make, so the two cannot come to count differently.
/// </summary>
internal static class OverviewReads
{
    /// <summary>The most scheduled publishes read per source. Past this the count stops, and so does the cost.</summary>
    internal const int MaxDuePerSource = 5000;

    /// <summary>
    /// The scheduled publishes due inside the period, and how many of them are overdue, over
    /// <paramref name="sources"/>. One capped query per source, split in memory.
    /// </summary>
    internal static async Task<(int Due, int Overdue)> CountDueAsync(
        IEnumerable<IQueryable<barakoCMS.Models.Content>> sources, DateTimeOffset now, CancellationToken ct,
        int cap = MaxDuePerSource)
    {
        var horizon = now.UtcDateTime.AddHours(OverviewRules.PeriodHours);
        var overdueBefore = now.UtcDateTime.AddMinutes(-OverviewRules.OverdueGraceMinutes);
        var due = 0;
        var overdue = 0;
        foreach (var source in sources)
        {
            var times = await source
                .Where(c => c.Status == ContentStatus.Scheduled && c.ScheduledPublishAt <= horizon)
                .OrderBy(c => c.ScheduledPublishAt)
                .Take(cap)
                .Select(c => c.ScheduledPublishAt)
                .ToListAsync(ct);
            due += times.Count;
            overdue += times.Count(t => t < overdueBefore);
        }

        return (due, overdue);
    }

    /// <summary>Every entry of every defined content type, for a read that holds <c>*</c>.</summary>
    internal static async Task<IReadOnlyList<IQueryable<barakoCMS.Models.Content>>> AllContentAsync(IQuerySession session, CancellationToken ct)
    {
        var types = await session.Query<ContentTypeDefinition>().Select(d => d.Name).ToListAsync(ct);
        return types.Count == 0
            ? []
            : [session.Query<barakoCMS.Models.Content>().Where(c => c.ContentType.IsOneOf(types.ToList()))];
    }

    /// <summary>The failed-runs rule, from the same day health the workflow page reads.</summary>
    internal static async Task<RuleResult> FailedRunsAsync(IQuerySession session, DateTimeOffset now, CancellationToken ct)
    {
        var day = await Workflows.GetWorkflowHealth.Endpoint.DayHealthAsync(session, now, ct);
        return OverviewRules.FailedRuns(day.Verdict, day.Finished - day.Succeeded, day.Finished);
    }
}
