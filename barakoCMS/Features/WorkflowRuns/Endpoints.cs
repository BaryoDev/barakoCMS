using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.WorkflowRuns;

internal sealed class RunResponse
{
    public Guid Id { get; init; }
    public Guid WorkflowDefinitionId { get; init; }
    public string WorkflowName { get; init; } = string.Empty;
    public Guid ContentId { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string TriggerEvent { get; init; } = string.Empty;
    public string Status { get; init; } = nameof(RunStatus.Pending);
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public List<AttemptResponse> Actions { get; init; } = new();

    public static RunResponse From(WorkflowRun r) => new()
    {
        Id = r.Id,
        WorkflowDefinitionId = r.WorkflowDefinitionId,
        WorkflowName = r.WorkflowName,
        ContentId = r.ContentId,
        ContentType = r.ContentType,
        TriggerEvent = r.TriggerEvent,
        Status = r.Status.ToString(),
        CreatedAt = r.CreatedAt,
        CompletedAt = r.CompletedAt,
        Actions = r.Actions.OrderBy(a => a.Ordinal).Select(AttemptResponse.From).ToList(),
    };
}

/// <summary>
/// One action's outcome as the API describes it.
/// </summary>
/// <remarks>
/// No response body and no resolved parameters. A 401 from an OAuth provider frequently contains
/// the credential that was sent, and a resolved parameter is where a connector's token would land
/// if one ever leaked into a template. The status code, the reason and the timing are what an
/// operator deciding whether to retry actually needs.
/// </remarks>
internal sealed class AttemptResponse
{
    public int Ordinal { get; init; }
    public string ActionType { get; init; } = string.Empty;
    public string Status { get; init; } = nameof(AttemptStatus.Pending);
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public int? ResponseStatus { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }

    public static AttemptResponse From(WorkflowActionAttempt a) => new()
    {
        Ordinal = a.Ordinal,
        ActionType = a.ActionType,
        Status = a.Status.ToString(),
        Attempts = a.Attempts,
        NextAttemptAt = a.NextAttemptAt,
        ResponseStatus = a.ResponseStatus,
        Error = a.Error,
        CompletedAt = a.CompletedAt,
        DurationMs = a.DurationMs,
    };
}

internal sealed class ListRunsRequest : ListRequest
{
    public string? Status { get; set; }
    public Guid? ContentId { get; set; }
}

internal static class RunGate
{
    /// <summary>
    /// The names that gated the run endpoints before capabilities, kept as the legacy fallback for
    /// both <see cref="SystemCapabilities.ViewWorkflowRuns"/> and
    /// <see cref="SystemCapabilities.RetryWorkflowActions"/>: the same pair gated reading and
    /// retrying, so the fallback preserves what the names already opened while the split decides
    /// what a role created at runtime can be given.
    /// </summary>
    internal static readonly string[] LegacyRoles = ["SuperAdmin", "Admin"];
}

internal sealed class ListRunsEndpoint : Endpoint<ListRunsRequest, PaginatedResponse<RunResponse>>
{
    private readonly IQuerySession _session;

    public ListRunsEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/workflow-runs");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListRunsRequest req, CancellationToken ct)
    {
        var query = _session.Query<WorkflowRun>().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<RunStatus>(req.Status, ignoreCase: true, out var status))
            {
                // Refused rather than ignored. A filter that is silently dropped returns more rows
                // than the caller asked for, and they cannot tell that from "no matches".
                ThrowError($"Status must be one of: {string.Join(", ", Enum.GetNames<RunStatus>())}.", 400);
                return;
            }

            query = query.Where(r => r.Status == status);
        }

        if (req.ContentId is { } contentId)
        {
            query = query.Where(r => r.ContentId == contentId);
        }

        var page = await query.OrderByDescending(r => r.CreatedAt).ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<RunResponse>
        {
            Items = page.Items.Select(RunResponse.From).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}

internal sealed class GetRunEndpoint : EndpointWithoutRequest<RunResponse>
{
    private readonly IQuerySession _session;

    public GetRunEndpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/workflow-runs/{id}");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("id"), out var id))
        {
            ThrowError("The run id is not a GUID.", 400);
            return;
        }

        var run = await _session.LoadAsync<WorkflowRun>(id, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(RunResponse.From(run), cancellation: ct);
    }
}

/// <summary>
/// Queues one action of a run to be attempted again.
/// </summary>
/// <remarks>
/// Deliberately does not execute. Pressing retry queues the attempt and the runner picks it up, so
/// a slow provider cannot hold an HTTP request open and a retry behaves exactly like the original.
///
/// An action that already succeeded is refused: the whole reason a run records each action
/// separately is so that retrying a failed third one does not re-send the first two.
/// </remarks>
internal sealed class RetryAttemptEndpoint : EndpointWithoutRequest<RunResponse>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public RetryAttemptEndpoint(
        IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/workflow-runs/{id}/actions/{ordinal}/retry");
        // Its own capability, not the one that reads runs. This queues a real attempt: the mail is
        // sent, the third party is called. Reading a run to answer "did it go out" must not carry
        // the ability to make it go out again.
        Definition.RequireCapability(SystemCapabilities.RetryWorkflowActions, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("id"), out var id))
        {
            ThrowError("The run id is not a GUID.", 400);
            return;
        }

        if (!int.TryParse(Route<string>("ordinal"), out var ordinal))
        {
            ThrowError("The ordinal is not a number.", 400);
            return;
        }

        var run = await _session.LoadAsync<WorkflowRun>(id, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var attempt = run.Actions.FirstOrDefault(a => a.Ordinal == ordinal);
        if (attempt is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (attempt.Status == AttemptStatus.Succeeded)
        {
            ThrowError("That action already succeeded. Retrying it would send it a second time.", 409);
            return;
        }

        if (attempt.Status == AttemptStatus.Running && attempt.LeaseExpiresAt > DateTimeOffset.UtcNow)
        {
            ThrowError("That action is running now. Wait for it to finish or for its lease to expire.", 409);
            return;
        }

        // Read before the reset. Retrying an Unknown is a decision to risk sending twice, and the
        // audit entry below is the record of who made it; reading the field afterwards would have
        // recorded every retry as ordinary.
        var wasUnknown = attempt.Status == AttemptStatus.Unknown;

        attempt.Status = AttemptStatus.Pending;
        attempt.NextAttemptAt = null;
        attempt.LeasedBy = null;
        attempt.LeaseExpiresAt = null;
        // The count is not reset. A retried action that keeps failing should still stop, and an
        // operator who wants more than the cap allows is asking for a different decision from the
        // one this button makes.
        run.CompletedAt = null;
        run.Recompute();
        _session.Update(run);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(_session, _tenant.Slug, "workflow.action.retried", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(WorkflowRun), targetId: run.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["workflow"] = run.WorkflowName,
                ["ordinal"] = ordinal,
                ["actionType"] = attempt.ActionType,
                ["wasUnknown"] = wasUnknown,
            }, ct: ct);

        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            // The run moved between the read above and this save, which in practice means the runner
            // claimed the attempt while the operator was deciding. Optimistic concurrency on
            // WorkflowRun already refuses the write, so nothing is lost; without this the refusal
            // reached the global handler as a 500 and read as a broken button rather than as the
            // guard working.
            //
            // 409 and the same wording as the running-lease check above, because from the operator's
            // side it is the same situation: somebody else got to it first.
            ThrowError("That action is running now. Wait for it to finish or for its lease to expire.", 409);
            return;
        }

        await Send.ResponseAsync(RunResponse.From(run), cancellation: ct);
    }
}
