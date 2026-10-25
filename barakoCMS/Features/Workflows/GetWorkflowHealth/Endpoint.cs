using barakoCMS.Features.WorkflowRuns;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Workflows.GetWorkflowHealth;

internal sealed class Request : PaginatedRequest
{
    /// <summary>How many days back the rate and the average look. Left out, seven. Clamped to what retention keeps.</summary>
    public int? WindowDays { get; set; }
}

internal sealed class Outcome
{
    public Guid RunId { get; init; }

    /// <summary>The name of a <see cref="RunStatus"/>.</summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(RunStatus))]
    public string Status { get; init; } = nameof(RunStatus.Succeeded);
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

internal sealed class WorkflowHealth
{
    public Guid WorkflowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; }

    /// <summary>The newest finished runs, newest first, up to ten, whatever the window. Pending and running runs are not outcomes; cancelled and abandoned runs are.</summary>
    public List<Outcome> LastOutcomes { get; init; } = new();

    /// <summary>
    /// Succeeded over succeeded, failed and partially failed, inside the window. Cancelled, pending
    /// and running runs are left out. Null when no run in the window has finished that way, which is
    /// not the same as 0 or 100.
    /// </summary>
    public double? SuccessRate { get; init; }

    /// <summary>The runs <see cref="SuccessRate"/> was taken over.</summary>
    public int FinishedRuns { get; init; }

    /// <summary>
    /// The mean of the recorded durations of the actions of the newest 200 runs in the window. An
    /// action with no recorded duration is left out. Null when none has one.
    /// </summary>
    public double? AverageActionDurationMs { get; init; }
}

internal sealed class RecentRun
{
    public Guid RunId { get; init; }
    public Guid WorkflowId { get; init; }

    /// <summary>The name of a <see cref="RunStatus"/>.</summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(RunStatus))]
    public string Status { get; init; } = nameof(RunStatus.Succeeded);

    /// <summary>ok, warn or bad. Null for a run that is pending, running or cancelled, which the rate leaves out.</summary>
    public string? Outcome { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class WorkflowDayHealth
{
    public Guid WorkflowId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Runs24h { get; init; }
    public int FinishedRuns { get; init; }

    /// <summary>From 0 to 1. Null when no run in the last 24 hours has finished.</summary>
    public double? SuccessRate { get; init; }

    /// <summary>ok, warn or bad.</summary>
    public string Verdict { get; init; } = WorkflowHealthRules.Ok;
}

internal sealed class HealthResponse : PaginatedResponse<WorkflowHealth>
{
    /// <summary>Every run created in the last 24 hours, whatever its status.</summary>
    public int Runs24h { get; set; }

    /// <summary>The newest ten runs of the tenant, newest first, whatever their age or status.</summary>
    public List<RecentRun> Recent { get; set; } = new();

    /// <summary>
    /// Succeeded over succeeded, failed and partially failed in the last 24 hours, from 0 to 1.
    /// Null when none has finished. Not the window of <see cref="WindowDays"/>.
    /// </summary>
    public double? SuccessRate { get; set; }

    /// <summary>ok at 90 percent and up, warn at 70 percent and up, otherwise bad. ok when nothing has finished.</summary>
    public string Verdict { get; set; } = WorkflowHealthRules.Ok;

    /// <summary>One line saying the same.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The same rate and verdict per workflow, for the workflows that ran in the last 24 hours. At most the 100 with the most runs, by name. A workflow whose definition is gone is left out.</summary>
    public List<WorkflowDayHealth> Workflows { get; set; } = new();

    /// <summary>The window used, in days, after it was clamped.</summary>
    public int WindowDays { get; set; }

    /// <summary>The start of that window.</summary>
    public DateTimeOffset WindowFrom { get; set; }
}

/// <summary>GET /api/workflows/health. Each workflow's recent outcomes, success rate and action time, paged by workflow.</summary>
/// <remarks>
/// Read with the runs, so it asks for <c>view_workflow_runs</c>. The window cannot reach past what
/// the retention sweep keeps: a longer one would divide the successes it kept by failures it did
/// not, and the response says the window it used.
/// </remarks>
internal sealed class Endpoint(IQuerySession session, IConfiguration config, TimeProvider clock) : Endpoint<Request, HealthResponse>
{
    internal const int DefaultWindowDays = 7;
    internal const int MaxWindowDays = 90;
    internal const int RecentOutcomes = 10;
    internal const int DurationSample = 200;

    public override void Configure()
    {
        Get("/api/workflows/health");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var window = Math.Clamp(req.WindowDays ?? DefaultWindowDays, 1, WindowCap(config));
        var now = clock.GetUtcNow();
        var from = now.AddDays(-window);

        var page = await session.Query<WorkflowDefinition>().OrderBy(w => w.Name).ThenBy(w => w.Id).ToPagedResponseAsync(req, ct);

        var ids = page.Items.Select(w => w.Id).ToList();
        var counts = await RunQueries.CountSinceAsync(
            session, ids, from, [RunStatus.Succeeded, RunStatus.Failed, RunStatus.PartiallyFailed], ct);

        var items = new List<WorkflowHealth>();
        foreach (var workflow in page.Items)
        {
            var recent = await RunQueries.RecentFinishedAsync(session, workflow.Id, RecentOutcomes, ct);
            var average = await RunQueries.AverageDurationAsync(session, workflow.Id, from, DurationSample, ct);
            items.Add(HealthOf(workflow, counts, recent, average));
        }

        var day = await DayHealthAsync(session, now, ct);

        await Send.ResponseAsync(new HealthResponse
        {
            Runs24h = day.Runs24h,
            Recent = day.Recent,
            SuccessRate = day.SuccessRate,
            Verdict = day.Verdict,
            Summary = day.Summary,
            Workflows = day.Workflows,
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
            WindowDays = window,
            WindowFrom = from,
        }, cancellation: ct);
    }

    internal const int MaxDayWorkflows = 100;

    internal sealed record DayHealth(
        int Runs24h, List<RecentRun> Recent, double? SuccessRate, string Verdict, string Summary,
        List<WorkflowDayHealth> Workflows, int Finished = 0, int Succeeded = 0);

    /// <summary>
    /// The tenant's last 24 hours as of <paramref name="now"/>: one grouped query per status off the
    /// CreatedAt index, the newest ten runs, and the names of the workflows that ran.
    /// </summary>
    internal static async Task<DayHealth> DayHealthAsync(IQuerySession session, DateTimeOffset now, CancellationToken ct)
    {
        var counts = await RunQueries.CountSinceByWorkflowAsync(session, now.AddHours(-24), Enum.GetValues<RunStatus>(), ct);
        var newest = await RunQueries.NewestAsync(session, RecentOutcomes, ct);

        var runs = counts.Values.Sum();
        var succeeded = counts.Where(c => c.Key.Status == RunStatus.Succeeded).Sum(c => c.Value);
        var finished = counts.Where(c => WorkflowHealthRules.CountedStatuses.Contains(c.Key.Status)).Sum(c => c.Value);
        var verdict = WorkflowHealthRules.Verdict(succeeded, finished);

        var top = counts.GroupBy(c => c.Key.Workflow)
            .Select(g => (Id: g.Key, Runs: g.Sum(c => c.Value)))
            .OrderByDescending(w => w.Runs).ThenBy(w => w.Id)
            .Take(MaxDayWorkflows)
            .Select(w => w.Id)
            .ToList();
        var names = top.Count == 0
            ? new Dictionary<Guid, string>()
            : (await session.LoadManyAsync<WorkflowDefinition>(ct, top)).ToDictionary(w => w.Id, w => w.Name);

        var workflows = top.Where(names.ContainsKey).Select(id =>
            {
                int Count(Func<RunStatus, bool> pick) => counts.Where(c => c.Key.Workflow == id && pick(c.Key.Status)).Sum(c => c.Value);
                var done = Count(WorkflowHealthRules.CountedStatuses.Contains);
                var won = Count(s => s == RunStatus.Succeeded);
                return new WorkflowDayHealth
                {
                    WorkflowId = id,
                    Name = names[id],
                    Runs24h = Count(_ => true),
                    FinishedRuns = done,
                    SuccessRate = WorkflowHealthRules.Rate(won, done),
                    Verdict = WorkflowHealthRules.Verdict(won, done),
                };
            })
            .OrderBy(w => w.Name, StringComparer.Ordinal).ThenBy(w => w.WorkflowId)
            .ToList();

        return new DayHealth(
            runs,
            newest.Select(r => new RecentRun
            {
                RunId = r.Id,
                WorkflowId = r.WorkflowDefinitionId,
                Status = r.Status.ToString(),
                Outcome = WorkflowHealthRules.Outcome(r.Status),
                CreatedAt = r.CreatedAt,
            }).ToList(),
            WorkflowHealthRules.Rate(succeeded, finished),
            verdict,
            WorkflowHealthRules.Summary(runs, succeeded, finished, verdict),
            workflows, finished, succeeded);
    }

    internal static int WindowCap(IConfiguration config)
    {
        var kept = WorkflowRunRetentionService.IsEnabled(config)
            ? WorkflowRunRetentionService.ReadWindows(config).SucceededDays
            : 0;

        return kept > 0 ? Math.Min(kept, MaxWindowDays) : MaxWindowDays;
    }

    private static WorkflowHealth HealthOf(
        WorkflowDefinition workflow,
        Dictionary<(Guid Workflow, RunStatus Status), int> counts,
        IEnumerable<WorkflowRun> recent,
        double? average)
    {
        int Count(RunStatus status) => counts.GetValueOrDefault((workflow.Id, status));

        var succeeded = Count(RunStatus.Succeeded);
        var finished = succeeded + Count(RunStatus.Failed) + Count(RunStatus.PartiallyFailed);

        return new WorkflowHealth
        {
            WorkflowId = workflow.Id,
            Name = workflow.Name,
            Enabled = workflow.Enabled,
            LastOutcomes = recent.Select(r => new Outcome
            {
                RunId = r.Id,
                Status = r.Status.ToString(),
                CreatedAt = r.CreatedAt,
                CompletedAt = r.CompletedAt,
            }).ToList(),
            SuccessRate = finished == 0 ? null : (double)succeeded / finished,
            FinishedRuns = finished,
            AverageActionDurationMs = average,
        };
    }
}
