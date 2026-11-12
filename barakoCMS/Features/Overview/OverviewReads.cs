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
                .Select(c => new { c.ScheduledPublishAt })
                .ToListAsync(ct);
            due += times.Count;
            overdue += times.Count(t => t.ScheduledPublishAt < overdueBefore);
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

    /// <summary>
    /// The 24 hour workflow day health and the failed-runs rule drawn from it, so the route and
    /// <see cref="NeedsYouSource"/> read one thing and the rule cannot come to differ.
    /// </summary>
    internal static async Task<(Workflows.GetWorkflowHealth.Endpoint.DayHealth Day, RuleResult Rule)> FailedRunsAsync(
        IQuerySession session, DateTimeOffset now, CancellationToken ct)
    {
        var day = await Workflows.GetWorkflowHealth.Endpoint.DayHealthAsync(session, now, ct);
        return (day, OverviewRules.FailedRuns(day.Verdict, day.Finished - day.Succeeded, day.Finished));
    }
}
