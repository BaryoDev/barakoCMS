using Cronos;
using JasperFx;
using Marten;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>One due time of one schedule that a host has taken. Its id is the claim.</summary>
/// <remarks>
/// Keyed by workflow id and due time, so a second host that reaches the same due time inserts the
/// same id and is refused by the database. Stored in the same save as the run it started, so there
/// is never a claim without a run or a run without a claim.
/// </remarks>
internal sealed class ScheduleClaim
{
    public string Id { get; set; } = string.Empty;
    public Guid WorkflowId { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime ClaimedAt { get; set; }
}

/// <summary>Reads a workflow's cron schedule and fires the runs that are due.</summary>
internal static class WorkflowSchedule
{
    public const string TenantZone = "tenant";
    public const string CatchUpOnce = "once";

    /// <summary>How many upcoming due times a save reports.</summary>
    public const int PreviewCount = 5;

    /// <summary>The expression, or null when it is not a valid 5-field cron expression.</summary>
    /// <remarks>
    /// Five fields exactly. Cronos would also accept six (with seconds), and a schedule finer than
    /// the one-minute sweep could not be kept.
    /// </remarks>
    public static CronExpression? Parse(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        if (expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length != 5) return null;

        return CronExpression.TryParse(expression, CronFormat.Standard, out var parsed) ? parsed : null;
    }

    /// <summary>Whether the zone setting is <c>tenant</c>, left out, or an IANA id this server knows.</summary>
    public static bool IsKnownZone(string? setting) =>
        string.IsNullOrWhiteSpace(setting)
        || string.Equals(setting, TenantZone, StringComparison.OrdinalIgnoreCase)
        || TemplateExpression.Zone(setting) is not null;

    /// <summary>The zone a schedule is read in. UTC for <c>tenant</c> when the site names none this server knows.</summary>
    public static async Task<TimeZoneInfo> ZoneAsync(IQuerySession session, WorkflowDefinition workflow, CancellationToken ct)
    {
        var setting = workflow.ScheduleTimeZone;

        if (!string.IsNullOrWhiteSpace(setting) && !string.Equals(setting, TenantZone, StringComparison.OrdinalIgnoreCase))
        {
            return TemplateExpression.Zone(setting) ?? TimeZoneInfo.Utc;
        }

        var site = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        return EntryUrls.Setting(site, "TimeZone") is { } name
            ? TemplateExpression.Zone(name) ?? TimeZoneInfo.Utc
            : TimeZoneInfo.Utc;
    }

    /// <summary>The next <paramref name="count"/> due times after <paramref name="after"/>, in UTC.</summary>
    public static List<DateTime> Next(CronExpression cron, TimeZoneInfo zone, DateTime after, int count)
    {
        var times = new List<DateTime>(count);
        var from = DateTime.SpecifyKind(after, DateTimeKind.Utc);

        while (times.Count < count && cron.GetNextOccurrence(from, zone, inclusive: false) is { } next)
        {
            times.Add(next);
            from = next;
        }

        return times;
    }

    /// <summary>The due time before <paramref name="due"/>, or null when the schedule has none within nine years.</summary>
    public static DateTime? Previous(CronExpression cron, TimeZoneInfo zone, DateTime due)
    {
        due = DateTime.SpecifyKind(due, DateTimeKind.Utc);

        // A widening window: an every-minute schedule is found in the first, and a yearly one on
        // Feb 29 in the last. Cronos only looks forward.
        for (var span = TimeSpan.FromHours(1); span <= TimeSpan.FromDays(366 * 9); span *= 4)
        {
            DateTime? last = null;
            foreach (var occurrence in cron.GetOccurrences(due - span, due, zone, fromInclusive: true, toInclusive: false))
            {
                last = occurrence;
            }

            if (last is not null) return last;
        }

        return null;
    }

    /// <summary>Starts one run for each schedule that has a due time since its last run.</summary>
    /// <returns>How many runs were queued.</returns>
    /// <remarks>
    /// Catch-up is <c>once</c>: a host that was stopped across several due times queues a run for
    /// the latest and none for the ones before it. The claim is for that latest time, so the next
    /// sweep counts from it. Each run and its claim are saved together; if the claim already exists
    /// another host took the time, and this one does nothing for it.
    /// </remarks>
    public static async Task<int> FireDueAsync(
        IDocumentSession session, DateTime nowUtc, ILogger? logger, CancellationToken ct)
    {
        var workflows = (await session.Query<WorkflowDefinition>()
            .Where(w => w.Schedule != null)
            .ToListAsync(ct)).Where(w => w.Enabled).ToList();

        var queued = 0;

        foreach (var workflow in workflows)
        {
            if (!WorkflowTriggers.Events(workflow).Contains(WorkflowEvents.Schedule, StringComparer.Ordinal)) continue;

            if (Parse(workflow.Schedule) is not { } cron)
            {
                logger?.LogWarning("Workflow {WorkflowId} has a schedule that is not a cron expression; skipped", workflow.Id);
                continue;
            }

            var zone = await ZoneAsync(session, workflow, ct);

            var lastClaim = (await session.Query<ScheduleClaim>()
                .Where(c => c.WorkflowId == workflow.Id)
                .OrderByDescending(c => c.DueAt)
                .Take(1)
                .ToListAsync(ct)).FirstOrDefault();

            var counted = new[] { lastClaim?.DueAt, workflow.ScheduleArmedAt }.Max() ?? nowUtc.AddMinutes(-1);

            // Armed or claimed after this sweep's clock, which another host's faster clock can do.
            if (counted >= nowUtc) continue;

            DateTime? previous = null;
            DateTime? due = null;
            foreach (var occurrence in cron.GetOccurrences(
                DateTime.SpecifyKind(counted, DateTimeKind.Utc), DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
                zone, fromInclusive: false, toInclusive: true))
            {
                previous = due;
                due = occurrence;
            }

            if (due is not { } latest) continue;

            previous ??= Previous(cron, zone, latest);

            if (await TryQueueAsync(session, workflow, latest, previous, nowUtc, logger, ct)) queued++;
        }

        return queued;
    }

    private static async Task<bool> TryQueueAsync(
        IDocumentSession session, WorkflowDefinition workflow, DateTime latest, DateTime? previous,
        DateTime nowUtc, ILogger? logger, CancellationToken ct)
    {
        var run = WorkflowRunQueue.NewRun(
            workflow, Guid.Empty, WorkflowTriggers.ContentTypes(workflow).FirstOrDefault() ?? string.Empty,
            WorkflowEvents.Schedule, eventSequence: 0);
        run.ScheduledFor = latest;
        run.ScheduledPreviousFor = previous;

        session.Insert(new ScheduleClaim
        {
            Id = $"{workflow.Id:N}:{latest:yyyyMMddTHHmmZ}",
            WorkflowId = workflow.Id,
            DueAt = latest,
            ClaimedAt = nowUtc,
        });
        session.Insert(run);

        try
        {
            await session.SaveChangesAsync(ct);
            WorkflowMetrics.Default.Queued(WorkflowEvents.Schedule, 1);
            return true;
        }
        catch (DocumentAlreadyExistsException)
        {
            session.EjectAllPendingChanges();
            logger?.LogDebug("Another host already took {DueAt:o} for workflow {WorkflowId}", latest, workflow.Id);
            return false;
        }
    }
}
