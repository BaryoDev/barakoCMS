using Microsoft.Extensions.Configuration;
using System.Globalization;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// Queues the runs of the schedule workflows whose time has come, once for each value of the date
/// field they watch.
/// </summary>
/// <remarks>
/// <para>
/// An entry is due when its date field plus the workflow's offset has passed, and for no longer
/// than <see cref="Grace"/> after that. The grace is what keeps a workflow made today from sending
/// every reminder for last year, and what lets a sweep that was down for an hour catch up.
/// </para>
/// <para>
/// A run's trigger key is the workflow, the entry and the date value. The same value is therefore
/// queued once however many sweeps see it, and an entry moved to a new date is queued again for
/// that date. A value that is missing, empty or not a date is not due and is not an error.
/// </para>
/// <para>
/// The read is a prefilter on the first ten characters of the field's text, compared as text, so no
/// cast can fail on a value like 2026-02-30, widened by a day each side for an offset in the text.
/// The exact window is applied to the parsed value afterwards.
/// </para>
/// </remarks>
internal static class ScheduleSweep
{
    /// <summary>How long after its fire time a run can still be queued, when nothing is set.</summary>
    internal static readonly TimeSpan Grace = TimeSpan.FromDays(7);

    /// <summary>How many entries one read takes, when nothing is set.</summary>
    internal const int DefaultPageSize = 200;

    /// <summary>The setting for the grace, in days.</summary>
    internal const string GraceDaysKey = "Workflows:ScheduleGraceDays";

    /// <summary>The setting for the page size.</summary>
    internal const string PageSizeKey = "Workflows:SchedulePageSize";

    /// <summary>The grace and page size from settings, each falling back to its default when missing or not above zero.</summary>
    internal static (TimeSpan Grace, int PageSize) SettingsFrom(Microsoft.Extensions.Configuration.IConfiguration? config)
    {
        var days = config?.GetValue<double?>(GraceDaysKey);
        var page = config?.GetValue<int?>(PageSizeKey);
        return (days is > 0 and <= 3650 ? TimeSpan.FromDays(days.Value) : Grace, page is > 0 ? page.Value : DefaultPageSize);
    }

    private const string PrefilterSql =
        @"substr(d.data -> 'Data' ->> ?, 1, 10) COLLATE ""C"" >= ? AND substr(d.data -> 'Data' ->> ?, 1, 10) COLLATE ""C"" <= ?";

    /// <returns>How many runs were queued.</returns>
    internal static async Task<int> RunAsync(
        IDocumentSession session, IWorkflowRunQueue queue, DateTime nowUtc, CancellationToken ct,
        Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        var (grace, pageSize) = SettingsFrom(config);
        var workflows = (await session.Query<WorkflowDefinition>().ToListAsync(ct))
            .Where(w => w.Enabled && w.DateSchedule is not null)
            .OrderBy(w => w.Name, StringComparer.Ordinal)
            .ToList();

        var queued = 0;
        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));

        foreach (var workflow in workflows)
        {
            if (!ScheduleOffset.TryParse(workflow.DateSchedule!.Offset, out var offset)
                || string.IsNullOrWhiteSpace(workflow.DateSchedule.ContentType)
                || string.IsNullOrWhiteSpace(workflow.DateSchedule.DateField))
            {
                continue;
            }

            // Fire time is value + offset, so it is due when value <= now - offset and still inside
            // the grace when value > now - offset - Grace.
            var latest = now - offset;
            var earliest = latest - grace;
            queued += await SweepWorkflowAsync(session, queue, workflow, earliest, latest, pageSize, ct);
        }

        return queued;
    }

    private static async Task<int> SweepWorkflowAsync(
        IDocumentSession session, IWorkflowRunQueue queue, WorkflowDefinition workflow,
        DateTimeOffset earliest, DateTimeOffset latest, int pageSize, CancellationToken ct)
    {
        var schedule = workflow.DateSchedule!;
        var field = schedule.DateField;
        var low = earliest.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var high = latest.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var extra = new WorkflowDefinition { Conditions = schedule.Where ?? [] };
        var queued = 0;
        var skipped = 0;
        var lowered = schedule.ContentType.ToLower();

        while (true)
        {
            IQueryable<barakoCMS.Models.Content> page = session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType.ToLower() == lowered)
                .Where(c => c.MatchesSql(PrefilterSql, field, low, field, high));

            if (schedule.InState is { Length: > 0 } state)
            {
                page = page.Where(c => c.LifecycleState == state);
            }

            var rows = await page.OrderBy(c => c.Id).Skip(skipped).Take(pageSize).ToListAsync(ct);

            foreach (var content in rows)
            {
                if (ValueOf(content, field) is not { } value || value <= earliest || value > latest) continue;
                if (!WorkflowConditions.Matches(workflow, content) || !WorkflowConditions.Matches(extra, content)) continue;

                var key = $"{workflow.Id:N}:{content.Id:N}:{value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
                if (await queue.EnqueueScheduledAsync(workflow, content, key, ct)) queued++;
            }

            if (rows.Count < pageSize) return queued;
            skipped += rows.Count;
        }
    }

    /// <summary>The date the field holds, or null when it is missing, empty or not a date.</summary>
    private static DateTimeOffset? ValueOf(barakoCMS.Models.Content content, string field)
    {
        if (content.Data is null || !content.Data.TryGetValue(field, out var raw)) return null;

        return DateTimeOffset.TryParse(raw?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}
