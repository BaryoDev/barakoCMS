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

    /// <summary>The newest finished runs, newest first, up to ten, whatever the window. Pending and running runs are not outcomes.</summary>
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

internal sealed class HealthResponse : PaginatedResponse<WorkflowHealth>
{
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
internal sealed class Endpoint(IQuerySession session, IConfiguration config) : Endpoint<Request, HealthResponse>
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
        var from = DateTimeOffset.UtcNow.AddDays(-window);

        var page = await session.Query<WorkflowDefinition>().OrderBy(w => w.Name).ToPagedResponseAsync(req, ct);

        var items = new List<WorkflowHealth>();
        foreach (var workflow in page.Items)
        {
            items.Add(await HealthOfAsync(workflow, from, ct));
        }

        await Send.ResponseAsync(new HealthResponse
        {
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
            WindowDays = window,
            WindowFrom = from,
        }, cancellation: ct);
    }

    internal static int WindowCap(IConfiguration config)
    {
        var kept = WorkflowRunRetentionService.IsEnabled(config)
            ? WorkflowRunRetentionService.ReadWindows(config).SucceededDays
            : 0;

        return kept > 0 ? Math.Min(kept, MaxWindowDays) : MaxWindowDays;
    }

    private async Task<WorkflowHealth> HealthOfAsync(WorkflowDefinition workflow, DateTimeOffset from, CancellationToken ct)
    {
        var id = workflow.Id;

        var recent = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == id && r.Status != RunStatus.Pending && r.Status != RunStatus.Running)
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentOutcomes)
            .ToListAsync(ct);

        var succeeded = await CountAsync(id, RunStatus.Succeeded, from, ct);
        var failed = await CountAsync(id, RunStatus.Failed, from, ct);
        var partial = await CountAsync(id, RunStatus.PartiallyFailed, from, ct);
        var finished = succeeded + failed + partial;

        var sampled = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == id && r.CreatedAt >= from)
            .OrderByDescending(r => r.CreatedAt)
            .Take(DurationSample)
            .ToListAsync(ct);

        var durations = sampled.SelectMany(r => r.Actions).Where(a => a.DurationMs is not null).Select(a => a.DurationMs!.Value).ToList();

        return new WorkflowHealth
        {
            WorkflowId = id,
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
            AverageActionDurationMs = durations.Count == 0 ? null : durations.Average(),
        };
    }

    private Task<int> CountAsync(Guid workflowId, RunStatus status, DateTimeOffset from, CancellationToken ct) =>
        session.Query<WorkflowRun>()
            .Where(r => r.WorkflowDefinitionId == workflowId && r.Status == status && r.CreatedAt >= from)
            .CountAsync(ct);
}
