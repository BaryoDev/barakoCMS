using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using barakoCMS.Features.Workflows;
using Marten;
using Marten.Linq.MatchesSql;

namespace barakoCMS.Features.WorkflowRuns;

internal sealed class RunResponse
{
    public Guid Id { get; init; }
    public Guid WorkflowDefinitionId { get; init; }
    public string WorkflowName { get; init; } = string.Empty;
    public Guid ContentId { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string TriggerEvent { get; init; } = string.Empty;

    /// <summary>What runs it. Null for the built-in runner.</summary>
    public string? Executor { get; init; }

    /// <summary>The name of a <see cref="RunStatus"/>.</summary>
    /// <remarks>
    /// A string on the wire. The attribute tells the OpenAPI document which names it can be, which
    /// a string alone does not say, and changes nothing that is serialised.
    /// </remarks>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(RunStatus))]
    public string Status { get; init; } = nameof(RunStatus.Pending);
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When the run was told to stop. Null for a run nobody stopped.</summary>
    public DateTimeOffset? CancelledAt { get; init; }

    /// <summary>
    /// The <c>X-Correlation-ID</c> of the request that caused the run. Null when no request did,
    /// and on a run queued before the id was kept.
    /// </summary>
    public string? CorrelationId { get; init; }
    public List<AttemptResponse> Actions { get; init; } = new();

    /// <summary>
    /// When the run last changed. Null on a run last written before this was kept. This is what
    /// <c>changedSince</c> on the list orders by.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The user who started the run by hand. Null for a run an event started.</summary>
    public Guid? StartedBy { get; init; }

    /// <summary>When an operator abandoned the run. Null for a run nobody abandoned.</summary>
    public DateTimeOffset? AbandonedAt { get; init; }

    /// <summary>
    /// Compensating, Compensated or Incomplete once the run is rolled back, and null for a run that
    /// never was. Incomplete is a run where something that went out was not undone.
    /// </summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(RunCompensation), IsNullable = true)]
    public string? Compensation { get; init; }

    public static RunResponse From(WorkflowRun r) => new()
    {
        Id = r.Id,
        WorkflowDefinitionId = r.WorkflowDefinitionId,
        WorkflowName = r.WorkflowName,
        ContentId = r.ContentId,
        ContentType = r.ContentType,
        TriggerEvent = r.TriggerEvent,
        Executor = r.Executor,
        Status = r.Status.ToString(),
        CreatedAt = r.CreatedAt,
        CompletedAt = r.CompletedAt,
        CancelledAt = r.CancelledAt,
        CorrelationId = barakoCMS.Infrastructure.Tracing.Correlation.Normalise(r.CorrelationId),
        UpdatedAt = r.UpdatedAt == default ? null : r.UpdatedAt,
        StartedBy = r.StartedBy,
        Actions = r.Actions.OrderBy(a => a.Ordinal).Select(AttemptResponse.From).ToList(),
        AbandonedAt = r.AbandonedAt,
        Compensation = r.Compensation?.ToString(),
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
    /// <summary>The name of an <see cref="AttemptStatus"/>.</summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(AttemptStatus))]
    public string Status { get; init; } = nameof(AttemptStatus.Pending);
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public int? ResponseStatus { get; init; }
    public string? Error { get; init; }
    public bool? Retryable { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>
    /// Every try of this action, oldest first, up to 20. A run written before the history was kept
    /// shows one entry built from what it has.
    /// </summary>
    public List<AttemptEntryResponse> History { get; init; } = new();

    /// <summary>How many older tries were dropped to stay within the cap. Zero until there are more than 20.</summary>
    public int DroppedAttempts { get; init; }

    /// <summary>Continue or Halt, as copied from the workflow when the run was queued.</summary>
    public string OnFailure { get; init; } = nameof(WorkflowFailurePolicy.Continue);

    /// <summary>
    /// On a Skipped action, the ordinal of the action whose failure skipped it. Null otherwise, and
    /// for an action skipped because the content no longer exists.
    /// </summary>
    public int? HaltedBy { get; init; }

    /// <summary>
    /// The outputs the action produced that the workflow captures, once it has succeeded. Null
    /// otherwise. Nothing the workflow did not name is ever stored, so nothing else can be here.
    /// </summary>
    public Dictionary<string, string>? Outputs { get; init; }

    /// <summary>
    /// The undoing of this action once the run is rolled back and it had succeeded. Null otherwise.
    /// A status of NotDeclared is an action nothing could undo.
    /// </summary>
    public CompensationResponse? Compensation { get; init; }

    public static AttemptResponse From(WorkflowActionAttempt a) => new()
    {
        Ordinal = a.Ordinal,
        ActionType = a.ActionType,
        Status = a.Status.ToString(),
        Attempts = a.Attempts,
        NextAttemptAt = a.NextAttemptAt,
        ResponseStatus = a.ResponseStatus,
        Error = a.Error,
        Retryable = a.Retryable,
        CompletedAt = a.CompletedAt,
        DurationMs = a.DurationMs,
        History = a.HistoryOrSynthetic(out var dropped).Select(AttemptEntryResponse.From).ToList(),
        DroppedAttempts = dropped,
        OnFailure = a.OnFailure.ToString(),
        HaltedBy = a.HaltedBy,
        Outputs = a.Outputs,
        Compensation = a.Compensation is { } undo ? CompensationResponse.From(undo, a.Compensate?.Type) : null,
    };
}

/// <summary>One try of an action. The error is the redacted one, the same text the attempt holds.</summary>
internal sealed class AttemptEntryResponse
{
    public int Number { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>The name of an <see cref="AttemptStatus"/>.</summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(AttemptStatus))]
    public string Outcome { get; init; } = nameof(AttemptStatus.Failed);
    public int? ResponseStatus { get; init; }
    public string? Error { get; init; }
    public bool? Retryable { get; init; }
    public long? DurationMs { get; init; }

    public static AttemptEntryResponse From(WorkflowAttemptEntry e) => new()
    {
        Number = e.Number,
        StartedAt = e.StartedAt,
        FinishedAt = e.FinishedAt,
        Outcome = e.Outcome.ToString(),
        ResponseStatus = e.ResponseStatus,
        Error = e.Error,
        Retryable = e.Retryable,
        DurationMs = e.DurationMs,
    };
}

/// <summary>One action's compensation, as the API describes it. No parameters, for the reason an attempt has none.</summary>
internal sealed class CompensationResponse
{
    /// <summary>The action type that undoes it. Null when none is declared.</summary>
    public string? ActionType { get; init; }

    /// <summary>The name of a <see cref="CompensationStatus"/>.</summary>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(CompensationStatus))]
    public string Status { get; init; } = nameof(CompensationStatus.Pending);
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long? DurationMs { get; init; }

    public static CompensationResponse From(CompensationState c, string? actionType) => new()
    {
        ActionType = actionType,
        Status = c.Status.ToString(),
        Attempts = c.Attempts,
        NextAttemptAt = c.NextAttemptAt,
        Error = c.Error,
        CompletedAt = c.CompletedAt,
        DurationMs = c.DurationMs,
    };
}

internal sealed class ListRunsRequest : ListRequest
{
    /// <summary>The name of a <see cref="RunStatus"/>, in any case. Left out, every status is listed.</summary>
    /// <remarks>
    /// Bound as a string so the handler keeps its own refusal for a name it does not know. Nullable
    /// is said on the attribute because without it the document marks the filter as required.
    /// </remarks>
    [NJsonSchema.Annotations.JsonSchemaType(typeof(RunStatus), IsNullable = true)]
    public string? Status { get; set; }
    public Guid? ContentId { get; set; }

    /// <summary>Only the runs of this workflow. Left out, every workflow's runs are listed.</summary>
    public Guid? WorkflowId { get; set; }

    /// <summary>
    /// A cursor from an earlier response, or a timestamp for the first call. Left out, the list is
    /// newest first as before. Given, only runs changed after it are listed, oldest change first.
    /// </summary>
    public string? ChangedSince { get; set; }
}

// The page fields are declared here and not inherited from PaginatedResponse, because a derived
// type is published as an allOf, which a contract diff reads as every page field removed.
/// <summary>A page of workflow runs with counts by status.</summary>
internal sealed class RunListResponse
{
    /// <summary>Items for the current page</summary>
    public IReadOnlyList<RunResponse> Items { get; set; } = Array.Empty<RunResponse>();

    /// <summary>Current page number (1-indexed)</summary>
    public int Page { get; set; }

    /// <summary>Number of items per page</summary>
    public int PageSize { get; set; }

    /// <summary>Total number of items across all pages</summary>
    public int TotalItems { get; set; }

    /// <summary>Total number of pages</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalItems / (double)PageSize) : 0;

    /// <summary>Whether there is a next page</summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>Whether there is a previous page</summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// How many runs have each status under the content and workflow filters, whatever the status filter and
    /// the page. Every status is present, with zero where there are none. Null when <c>changedSince</c>
    /// was given, since a poll reads what changed and a count would add a scan to each one.
    /// </summary>
    public Dictionary<string, int>? StatusCounts { get; set; }

    /// <summary>
    /// Pass back as <c>changedSince</c> to read what changed after this response. Present only when
    /// <c>changedSince</c> was given.
    /// </summary>
    public string? Cursor { get; set; }

    /// <summary>
    /// Succeeded runs that finished before this are removed by the retention sweep. Null when the
    /// sweep is off or keeps them forever.
    /// </summary>
    public DateTimeOffset? SucceededRetainedFrom { get; set; }

    /// <summary>The same for failed, partially failed and cancelled runs.</summary>
    public DateTimeOffset? FailedRetainedFrom { get; set; }
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
    internal static readonly IReadOnlyList<string> LegacyRoles = CapabilityGate.AdminLegacyRoles;
}

internal sealed class ListRunsEndpoint(
    IQuerySession session,
    IConfiguration config,
    TimeProvider clock) : Endpoint<ListRunsRequest, RunListResponse>
{
    public override void Configure()
    {
        Get("/api/workflow-runs");
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(ListRunsRequest req, CancellationToken ct)
    {
        RunStatus? status = null;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<RunStatus>(req.Status, ignoreCase: true, out var parsed))
            {
                // Refused rather than ignored. A filter that is silently dropped returns more rows
                // than the caller asked for, and they cannot tell that from "no matches".
                ThrowError($"Status must be one of: {string.Join(", ", Enum.GetNames<RunStatus>())}.", 400);
                return;
            }

            status = parsed;
        }

        RunCursor? since = null;
        if (req.ChangedSince is not null)
        {
            if (!RunCursor.TryParse(req.ChangedSince, out var cursor))
            {
                ThrowError("ChangedSince must be a cursor from an earlier response or a timestamp.", 400);
                return;
            }

            since = cursor;
        }

        var now = clock.GetUtcNow();
        var counts = since is null
            ? await RunQueries.CountByStatusAsync(session, req.ContentId, req.WorkflowId, ct)
            : null;

        var query = session.Query<WorkflowRun>().AsQueryable();
        if (status is { } s) query = query.Where(r => r.Status == s);
        if (req.ContentId is { } contentId) query = query.Where(r => r.ContentId == contentId);
        if (req.WorkflowId is { } workflowId) query = query.Where(r => r.WorkflowDefinitionId == workflowId);

        if (since is { } from)
        {
            query = query
                .Where(r => r.MatchesSql(RunCursor.AfterSql, from.At.UtcDateTime, from.At.UtcDateTime, from.Id))
                .OrderBy(r => r.UpdatedAt)
                .ThenBy(r => r.Id);
        }
        else
        {
            query = query.OrderByDescending(r => r.CreatedAt);
        }

        var page = await query.ToPagedResponseAsync(req, ct);

        var windows = WorkflowRunRetentionService.ReadWindows(config);
        var sweeping = WorkflowRunRetentionService.IsEnabled(config);
        var items = page.Items.Select(RunResponse.From).ToList();

        var etag = RunListETag.Compute(HttpContext, items, page.Page, page.PageSize, page.TotalItems, counts,
            $"{sweeping}:{windows.SucceededDays}:{windows.FailedDays}");
        HttpContext.Response.Headers.ETag = etag;
        HttpContext.Response.Headers.CacheControl = "private, no-cache";
        if (DeliveryCache.IsNotModified(HttpContext.Request, etag, null))
        {
            HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;
            // Completing the response is what stops FastEndpoints from sending its own 204 once HandleAsync returns.
            await HttpContext.Response.CompleteAsync();
            return;
        }

        await Send.ResponseAsync(new RunListResponse
        {
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
            StatusCounts = counts,
            Cursor = since is { } input
                ? RunCursor.Next(input, page.Items, req.Skip + page.Items.Count < page.TotalItems, now).ToString()
                : null,
            SucceededRetainedFrom = sweeping && windows.SucceededDays > 0 ? now.AddDays(-windows.SucceededDays) : null,
            FailedRetainedFrom = sweeping && windows.FailedDays > 0 ? now.AddDays(-windows.FailedDays) : null,
        }, cancellation: ct);
    }
}

internal sealed class GetRunEndpoint(IQuerySession session) : EndpointWithoutRequest<RunResponse>
{
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

        var run = await session.LoadAsync<WorkflowRun>(id, ct);
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
///
/// Retrying an action that halted its run is the resume: the actions its failure skipped are queued
/// again behind it, and run once it succeeds. One of those skipped actions is refused on its own,
/// since running it is running past the failure.
/// </remarks>
internal sealed class RetryAttemptEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<RunResponse>
{
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

        var run = await session.LoadAsync<WorkflowRun>(id, ct);
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

        if (Refusal(run, attempt) is { } refusal)
        {
            ThrowError(refusal, 409);
            return;
        }

        // The runner cancels a run whose workflow is switched off, so a retry accepted here would
        // send nothing and leave the run Cancelled, which can never be retried again. Refused
        // before anything is written, so the run keeps its status and can be retried once the
        // workflow is on. A workflow that was deleted must not fire again through a retry either.
        //
        // Under the lock a delete of the workflow takes, so the check below and the save cannot
        // straddle one: the delete either sees the attempt queued here and cancels it, or has
        // already committed and the workflow is gone.
        await barakoCMS.Features.Workflows.WorkflowDefinitionLock.TakeAsync(session, run.WorkflowDefinitionId, ct);

        var workflow = await session.LoadAsync<WorkflowDefinition>(run.WorkflowDefinitionId, ct);
        if (workflow is null)
        {
            ThrowError("The workflow that run belonged to has been deleted, so its actions are not run again.", 409);
            return;
        }

        if (!workflow.Enabled)
        {
            ThrowError("That workflow is switched off. Switch it on first, then retry.", 409);
            return;
        }

        // Read before the reset. Retrying an Unknown is a decision to risk sending twice, and the
        // audit entry below is the record of who made it; reading the field afterwards would have
        // recorded every retry as ordinary.
        var wasUnknown = attempt.Status == AttemptStatus.Unknown;

        // The same decision for a failure the runner recorded as permanent: nothing about it changes
        // on its own, and a Conditional marked permanent re-sends the child that already went out.
        // Allowed, because an operator who fixed the configuration has a reason to re-drive it, and
        // recorded, so the audit trail says it was retried knowing that.
        //
        // A failure recorded before retryable existed, or by a path that did not set it, says
        // nothing either way. The audit entry then leaves wasPermanent out rather than claiming the
        // failure was transient, which keeps the value a boolean wherever it is present.
        bool? wasPermanent = attempt.Status != AttemptStatus.Failed
            ? false
            : attempt.Retryable is { } retryable ? !retryable : null;

        // Counted before the reset too. Retrying an action that halted its run queues the actions
        // its failure skipped, and the audit entry says how many more this retry set going.
        var skippedBehind = run.Actions.Count(a => a.Status == AttemptStatus.Skipped && a.HaltedBy == ordinal);

        attempt.Status = AttemptStatus.Pending;
        attempt.Retryable = null;
        attempt.NextAttemptAt = null;
        attempt.LeasedBy = null;
        attempt.LeaseExpiresAt = null;
        // The count is not reset. A retried action that keeps failing should still stop, and an
        // operator who wants more than the cap allows is asking for a different decision from the
        // one this button makes.
        run.CompletedAt = null;
        run.ResumeAfter(ordinal);
        run.Recompute();
        session.Update(run);

        var metadata = new Dictionary<string, object>
        {
            ["workflow"] = run.WorkflowName,
            ["ordinal"] = ordinal,
            ["actionType"] = attempt.ActionType,
            ["wasUnknown"] = wasUnknown,
        };
        if (skippedBehind > 0) metadata["resumedActions"] = skippedBehind;
        if (wasPermanent is { } permanent) metadata["wasPermanent"] = permanent;

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(session, tenant.Slug, "workflow.action.retried", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(WorkflowRun), targetId: run.Id.ToString(),
            metadata: metadata, ct: ct);

        try
        {
            await session.SaveChangesAsync(ct);
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

    /// <summary>Why this attempt of the run cannot be retried as it stands, or null when it can.</summary>
    /// <remarks>
    /// A cancelled run is refused as a whole, not only its cancelled actions. The runner starts
    /// nothing on a run that was stopped, so an attempt queued here would be cancelled again at the
    /// next claim, and the workflow behind it may be switched off or gone. A run being rolled back
    /// is refused because going forward again would redo what the rollback is undoing.
    /// </remarks>
    private static string? Refusal(WorkflowRun run, WorkflowActionAttempt attempt) =>
        run.CancelledAt is not null ? "That run was cancelled, and a cancelled run is not started again."
        : attempt.Status == AttemptStatus.Succeeded ? "That action already succeeded. Retrying it would send it a second time."
        : run.CompensationStartedAt is not null ? "That run is being rolled back, or was, so its actions are not run again."
        : attempt.Status == AttemptStatus.Running && attempt.LeaseExpiresAt > DateTimeOffset.UtcNow
            ? "That action is running now. Wait for it to finish or for its lease to expire."
        : attempt.Status == AttemptStatus.Skipped && attempt.HaltedBy is { } haltedBy
            ? $"That action was skipped because action {haltedBy} failed and is set to halt the run. "
              + "Retry that action, and this one runs after it."
        : null;
}

/// <summary>
/// Stops one run: every action that has not started is cancelled and nothing more is started.
/// </summary>
/// <remarks>
/// An action running under a live lease is left. Its request is already with the third party, so
/// it finishes and records its outcome, and the run ends after it. If it fails it is not queued
/// again. An action still marked running after its lease ran out becomes Unknown: it was claimed,
/// so it may have gone out, and Cancelled is kept for an action that never did.
///
/// The write is the same optimistic one the runner's claim makes on the run, so a cancel and a
/// claim of the same run cannot both be saved. Whichever is second is refused: the runner moves on,
/// and this answers 409 so the request can be sent again for what is left.
/// </remarks>
internal sealed class CancelRunEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<RunResponse>
{
    public override void Configure()
    {
        Post("/api/workflow-runs/{id}/cancel");
        // With authoring, not with retry. Whoever can switch a workflow off or delete it already
        // stops its runs that way, and retry is the grant that makes an action happen, which this
        // never does.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("id"), out var id))
        {
            ThrowError("The run id is not a GUID.", 400);
            return;
        }

        var run = await session.LoadAsync<WorkflowRun>(id, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (run.Status is not (RunStatus.Pending or RunStatus.Running))
        {
            ThrowError("That run has already finished, so there is nothing left to cancel.", 409);
            return;
        }

        var alreadyStopped = run.CancelledAt is not null;
        var cancelled = run.Cancel(DateTimeOffset.UtcNow);

        // A repeat while the last action is still out changes nothing, so it writes nothing and
        // records nothing, and answers with the run as it stands.
        if (alreadyStopped && cancelled == 0)
        {
            await Send.ResponseAsync(RunResponse.From(run), cancellation: ct);
            return;
        }

        session.Update(run);

        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        await AuditLog.RecordAsync(session, tenant.Slug, "workflow.run.cancelled", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(WorkflowRun), targetId: run.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["workflow"] = run.WorkflowName,
                ["cancelledActions"] = run.Actions.Count(a => a.Status == AttemptStatus.Cancelled),
                // An action left running is the one thing this could not stop, so the entry says
                // whether there was one.
                ["leftRunning"] = run.Actions.Count(a => a.Status == AttemptStatus.Running),
            }, ct: ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            ThrowError("The runner took an action of that run just now. Nothing was changed. Send the request again to cancel what is left.", 409);
            return;
        }

        await Send.ResponseAsync(RunResponse.From(run), cancellation: ct);
    }
}

/// <summary>
/// Gives up on a run and undoes what it did: what has not started is cancelled, and each action
/// that succeeded has its compensation run, the last first.
/// </summary>
/// <remarks>
/// For a run nobody intends to finish, such as one whose halting action ended Unknown, or that
/// failed part way and has to be rolled back by hand. An action that succeeded and declares no
/// compensation is reported as not undone, and the run then reads Incomplete.
///
/// Refused while an action is in flight under a live lease, since the run cannot be undone around
/// a request that may still arrive; for a run already stopped, already rolled back, or finished
/// with every action succeeded; and for a workflow that is switched off or deleted, the same as a
/// retry, because a compensation is an action that goes out.
/// </remarks>
internal sealed class AbandonRunEndpoint(
    IDocumentSession session,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<RunResponse>
{
    public override void Configure()
    {
        Post("/api/workflow-runs/{id}/abandon");
        // With retry, not with cancel: this sends requests to third parties, which cancelling never does.
        Definition.RequireCapability(SystemCapabilities.RetryWorkflowActions, RunGate.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("id"), out var id))
        {
            ThrowError("The run id is not a GUID.", 400);
            return;
        }

        var run = await session.LoadAsync<WorkflowRun>(id, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (Refusal(run, now) is { } refusal)
        {
            ThrowError(refusal, 409);
            return;
        }

        await barakoCMS.Features.Workflows.WorkflowDefinitionLock.TakeAsync(session, run.WorkflowDefinitionId, ct);

        var workflow = await session.LoadAsync<WorkflowDefinition>(run.WorkflowDefinitionId, ct);
        if (workflow is null)
        {
            ThrowError("The workflow that run belonged to has been deleted, so nothing of it is run again.", 409);
            return;
        }

        if (!workflow.Enabled)
        {
            ThrowError("That workflow is switched off. Switch it on first, then abandon the run.", 409);
            return;
        }

        // A running attempt whose lease ran out may have gone out, so it is Unknown and not undone.
        foreach (var attempt in run.Actions.Where(a => a.Status == AttemptStatus.Running))
        {
            attempt.Status = AttemptStatus.Unknown;
            attempt.Error = WorkflowRun.InFlightWhenStopped;
            attempt.LeasedBy = null;
            attempt.LeaseExpiresAt = null;
            attempt.CompletedAt = now;
        }

        run.AbandonedAt = now;
        var queued = run.StartCompensation(now);
        run.Recompute();
        session.Update(run);

        await RecordAsync(run, queued, ct);

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException
            || ex.GetType().Name.Contains("Concurrency"))
        {
            ThrowError("The runner took an action of that run just now. Nothing was changed. Send the request again.", 409);
            return;
        }

        await Send.ResponseAsync(RunResponse.From(run), cancellation: ct);
    }

    /// <summary>Why the run cannot be abandoned as it stands, or null when it can.</summary>
    /// <remarks>
    /// A run that finished with every action succeeded was meant to finish, and undoing it weeks
    /// later is not what abandoning is for.
    /// </remarks>
    private static string? Refusal(WorkflowRun run, DateTimeOffset now) =>
        run.CompensationStartedAt is not null ? "That run is already being rolled back, or was."
        : run.CancelledAt is not null ? "That run was cancelled, and a cancelled run is not started again."
        : run.Status == RunStatus.Succeeded ? "That run finished and every action of it succeeded, so it is not abandoned."
        : run.Actions.Any(a => a.Status == AttemptStatus.Running && a.LeaseExpiresAt > now)
            ? "An action of that run is running now. Wait for it to finish or for its lease to expire."
        : !run.Actions.Any(a => a.Status == AttemptStatus.Succeeded)
            ? "Nothing in that run succeeded, so there is nothing to undo. Cancel it instead."
        : null;

    private Task RecordAsync(WorkflowRun run, int queued, CancellationToken ct)
    {
        var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
        return AuditLog.RecordAsync(session, tenant.Slug, "workflow.run.abandoned", actorId,
            User.FindFirst("Username")?.Value,
            targetType: nameof(WorkflowRun), targetId: run.Id.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["workflow"] = run.WorkflowName,
                ["compensations"] = queued,
                ["notDeclared"] = run.Actions.Count(a => a.Compensation?.Status == CompensationStatus.NotDeclared),
            }, ct: ct);
    }
}
