using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace barakoCMS.Features.Overview;

/// <summary>One link from an item to the place a person fixes it. The client maps a kind to a route.</summary>
internal sealed class OverviewRef
{
    /// <summary>mailBounces, workflowRuns, content or runner.</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Id { get; init; }
}

/// <summary>An item in the list of things that need a person.</summary>
internal sealed class NeedsYouItem
{
    /// <summary>Stable across responses while the cause lasts, and the id the resolve route takes.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>warn or bad.</summary>
    public string Severity { get; init; } = OverviewRules.Warn;

    public string Text { get; init; } = string.Empty;
    public OverviewRef Ref { get; init; } = new();

    /// <summary>Whether <c>POST /api/overview/items/{id}/resolve</c> clears it.</summary>
    public bool Resolvable { get; init; }
}

/// <summary>One cell of the health strip.</summary>
internal sealed class StripItem
{
    /// <summary>API, Database, Storage, Mail or Runner.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>ok, warn or bad.</summary>
    public string Status { get; init; } = OverviewRules.Ok;

    /// <summary>The version, for API.</summary>
    public string? Version { get; init; }

    /// <summary>How long the check took, in milliseconds, for Database.</summary>
    public long? Ms { get; init; }

    /// <summary>One plain line, for a cell that is not ok and for Mail.</summary>
    public string? Detail { get; init; }
}

/// <summary>What one rule found. A rule fills the strip, the list, or both, and both carry its key.</summary>
internal sealed record RuleResult(string Key, string Status, StripItem? Strip = null, NeedsYouItem? Item = null);

/// <summary>
/// Every rule behind GET /api/overview, one function each, written down in docs/overview.md. Plain
/// facts in, a status and a line out. Nothing here reads a store or calls a model, so a written
/// summary built elsewhere reads the same facts the response does.
/// </summary>
internal static class OverviewRules
{
    internal const string Ok = "ok";
    internal const string Warn = "warn";
    internal const string Bad = "bad";

    /// <summary>A database check slower than this many milliseconds is warn.</summary>
    internal const long SlowDatabaseMs = 500;

    /// <summary>A database check slower than this many milliseconds is bad.</summary>
    internal const long VerySlowDatabaseMs = 2000;

    /// <summary>A scheduled publish this many minutes past its time, and still waiting, is overdue.</summary>
    internal const int OverdueGraceMinutes = 5;

    /// <summary>The window every count and its prior period cover, in hours.</summary>
    internal const int PeriodHours = 24;

    internal const string BouncesItemId = "mail.bounces";
    internal const string WorkflowsItemId = "workflows.failing";
    internal const string OverdueItemId = "content.overdue";
    internal const string RunnerItemId = "runner.stalled";

    internal static string FromHealth(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => Ok,
        HealthStatus.Degraded => Warn,
        _ => Bad,
    };

    /// <summary>API is ok when it answers, and carries the version.</summary>
    internal static RuleResult Api(string? version) =>
        new("api", Ok, new StripItem { Name = "API", Status = Ok, Version = version });

    /// <summary>Database follows its health check, and a slow answer is warn or bad on its own.</summary>
    internal static RuleResult Database(HealthStatus check, long ms)
    {
        var status = Worst(FromHealth(check), ms >= VerySlowDatabaseMs ? Bad : ms >= SlowDatabaseMs ? Warn : Ok);
        return new("database", status, new StripItem
        {
            Name = "Database",
            Status = status,
            Ms = ms,
            Detail = status == Ok ? null : "The database is slow or not answering.",
        });
    }

    /// <summary>Storage follows the disk space check.</summary>
    internal static RuleResult Storage(HealthStatus check)
    {
        var status = FromHealth(check);
        return new("storage", status, new StripItem
        {
            Name = "Storage",
            Status = status,
            Detail = status == Ok ? null : "Free disk space is low.",
        });
    }

    /// <summary>
    /// Runner follows the workflow projection check. A shard that has stopped means no workflow fires,
    /// so a status that is not ok is also an item.
    /// </summary>
    internal static RuleResult Runner(HealthStatus check)
    {
        var status = FromHealth(check);
        const string text = "The workflow projection is behind or stopped, so workflows may not run.";
        return new("runner", status,
            new StripItem { Name = "Runner", Status = status, Detail = status == Ok ? null : text },
            status == Ok
                ? null
                : new NeedsYouItem
                {
                    Id = RunnerItemId, Severity = status, Text = text,
                    Ref = new OverviewRef { Kind = "runner" }, Resolvable = false,
                });
    }

    /// <summary>Mail is delivering until a bounce is waiting for someone, then warn.</summary>
    internal static RuleResult Mail(int unresolvedBounces)
    {
        var status = unresolvedBounces > 0 ? Warn : Ok;
        return new("mail", status, new StripItem
        {
            Name = "Mail",
            Status = status,
            Detail = status == Ok ? "Delivering." : $"{unresolvedBounces} {Plural(unresolvedBounces, "bounce")} waiting.",
        });
    }

    /// <summary>One resolvable item while any bounce is unresolved. Resolving it clears Mail too.</summary>
    internal static RuleResult Bounces(int unresolvedBounces) =>
        unresolvedBounces <= 0
            ? new("mail", Ok)
            : new("mail", Warn, Item: new NeedsYouItem
            {
                Id = BouncesItemId,
                Severity = Warn,
                Text = $"{unresolvedBounces} email {Plural(unresolvedBounces, "bounce")} to look at.",
                Ref = new OverviewRef { Kind = "mailBounces" },
                Resolvable = true,
            });

    /// <summary>
    /// An item when the last 24 hours of workflow runs are not ok, with the verdict
    /// <c>WorkflowHealthRules</c> gave. Not resolvable: it clears when the runs recover.
    /// </summary>
    internal static RuleResult FailedRuns(string verdict, int failed, int finished) =>
        verdict == Ok
            ? new("workflows", Ok)
            : new("workflows", verdict, Item: new NeedsYouItem
            {
                Id = WorkflowsItemId,
                Severity = verdict,
                Text = $"{failed} of {finished} workflow {Plural(finished, "run")} failed in the last 24 hours.",
                Ref = new OverviewRef { Kind = "workflowRuns" },
                Resolvable = false,
            });

    /// <summary>An item when entries are still scheduled for a time already past by the grace.</summary>
    internal static RuleResult OverduePublishes(int overdue) =>
        overdue <= 0
            ? new("publishes", Ok)
            : new("publishes", Warn, Item: new NeedsYouItem
            {
                Id = OverdueItemId,
                Severity = Warn,
                Text = $"{overdue} scheduled {Plural(overdue, "publish", "publishes")} past due.",
                Ref = new OverviewRef { Kind = "content" },
                Resolvable = false,
            });

    /// <summary>The worst of the statuses: bad, then warn, then ok. Nothing to look at is ok.</summary>
    internal static string Verdict(IEnumerable<string> statuses)
    {
        var verdict = Ok;
        foreach (var status in statuses) verdict = Worst(verdict, status);
        return verdict;
    }

    /// <summary>The pill: how many rules are not ok, called warnings, or problems when any is bad, or that all are ok.</summary>
    internal static string Headline(IEnumerable<RuleResult> results)
    {
        var notOk = results.Where(r => r.Status != Ok).ToList();
        var count = notOk.Select(r => r.Key).Distinct().Count();
        if (count == 0) return "All systems normal";
        return $"{count} {Plural(count, notOk.Any(r => r.Status == Bad) ? "problem" : "warning")}";
    }

    /// <summary>Bad first, then warn, then by id, so the list reads in severity order.</summary>
    internal static List<NeedsYouItem> Order(IEnumerable<NeedsYouItem> items) =>
        items.OrderBy(i => i.Severity == Bad ? 0 : 1).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();

    private static string Worst(string a, string b) =>
        a == Bad || b == Bad ? Bad : a == Warn || b == Warn ? Warn : Ok;

    private static string Plural(int n, string word, string? plural = null) => n == 1 ? word : plural ?? word + "s";
}
