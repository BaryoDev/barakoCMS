namespace barakoCMS.Features.Overview.Get;

/// <summary>A count for the last 24 hours and the same count for the 24 hours before, for "+12% vs prior".</summary>
internal sealed class PeriodCount
{
    public int Current { get; init; }

    /// <summary>Null when there is no earlier period to read.</summary>
    public int? Prior { get; init; }
}

/// <summary>Each count is null when the caller lacks what reads it, or the source is not installed.</summary>
internal sealed class OverviewCounts
{
    /// <summary>
    /// Scheduled entries the caller may read whose publish time falls in the next 24 hours, overdue
    /// ones included. Prior is null: running a publish clears its time, so there is no earlier period
    /// to count. Needs content read.
    /// </summary>
    public PeriodCount? PublishesDue { get; init; }

    /// <summary>Failed and partially failed workflow runs created in the period. Needs <c>view_workflow_runs</c>.</summary>
    public PeriodCount? FailedRuns { get; init; }

    /// <summary>Bounces recorded in the period. Needs <c>manage_email_settings</c> and an email module that reports them.</summary>
    public PeriodCount? Bounces { get; init; }

    /// <summary>Successful sign-ins to the tenant in the period. Needs <c>view_audit_log</c>.</summary>
    public PeriodCount? SignIns { get; init; }
}

internal sealed class WorkflowOverview
{
    public int Runs24h { get; init; }

    /// <summary>From 0 to 1, null when no run in the last 24 hours has finished.</summary>
    public double? SuccessRate { get; init; }

    /// <summary>ok, warn or bad, the verdict of <c>GET /api/workflows/health</c>.</summary>
    public string Verdict { get; init; } = OverviewRules.Ok;

    public string Summary { get; init; } = string.Empty;
}

internal sealed class HealthStripResponse
{
    /// <summary>The worst status in <see cref="Items"/>.</summary>
    public string Verdict { get; init; } = OverviewRules.Ok;

    /// <summary>API, Database, Storage and Runner, and Mail when an email module reports bounces.</summary>
    public List<StripItem> Items { get; init; } = new();
}

internal sealed class OverviewResponse
{
    /// <summary>ok, warn or bad: the worst status among the parts the caller may see.</summary>
    public string Verdict { get; init; } = OverviewRules.Ok;

    /// <summary>The pill text: "All systems normal", or a count such as "1 warning".</summary>
    public string Headline { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; }

    public OverviewCounts Counts { get; init; } = new();

    /// <summary>Null without <c>view_workflow_runs</c>.</summary>
    public WorkflowOverview? Workflows { get; init; }

    /// <summary>Null without <c>view_monitoring</c>.</summary>
    public HealthStripResponse? Health { get; init; }

    /// <summary>Worst first, then by id. Only items the caller may see.</summary>
    public List<NeedsYouItem> NeedsYou { get; init; } = new();
}
