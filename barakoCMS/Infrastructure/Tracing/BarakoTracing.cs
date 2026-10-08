using System.Diagnostics;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Tracing;

/// <summary>The spans barakoCMS starts itself, and the attributes it puts on any span.</summary>
/// <remarks>
/// With nothing listening, <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// null and none of this costs more than that call. A host that runs its own OpenTelemetry setup
/// gets these spans by adding the source named <see cref="SourceName"/>.
///
/// Every attribute written here is an id, a number or a name an administrator chose. No action
/// parameter, no error text, no content and no person: an action's parameters hold URLs and
/// credentials, and a provider's error can quote what was sent to it.
/// </remarks>
internal static class BarakoTracing
{
    public const string SourceName = "BarakoCMS";

    public const string WorkflowActionSpan = "workflow.action";

    public const string CorrelationIdTag = "barako.correlation_id";
    public const string TenantTag = "barako.tenant";
    public const string WorkflowIdTag = "barako.workflow.id";
    public const string WorkflowRunIdTag = "barako.workflow.run_id";
    public const string WorkflowTriggerTag = "barako.workflow.trigger";
    public const string WorkflowActionTag = "barako.workflow.action";
    public const string WorkflowOrdinalTag = "barako.workflow.action_ordinal";
    public const string WorkflowAttemptTag = "barako.workflow.attempt";
    public const string WorkflowOutcomeTag = "barako.workflow.outcome";

    public const string JobClaimSpan = "job.claim";
    public const string JobRunSpan = "job.run";
    public const string JobFinishSpan = "job.finish";
    public const string ScheduledSweepSpan = "scheduler.sweep";
    public const string ScheduledTenantSpan = "scheduler.tenant";
    public const string CollectionSyncSweepSpan = "collection_sync.sweep";
    public const string CollectionSyncRunSpan = "collection_sync.run";
    public const string OutboundCallSpan = "outbound.call";
    public const string OutboundTrySpan = "outbound.try";

    /// <summary>The event on an <see cref="OutboundCallSpan"/> for each retry Carom decided to make.</summary>
    public const string RetryEvent = "retry";

    public const string JobQueueTag = "barako.job.queue";
    public const string JobClaimedTag = "barako.job.claimed";
    public const string JobIdTag = "barako.job.id";
    public const string JobCommandTag = "barako.job.command";
    public const string JobAttemptTag = "barako.job.attempt";
    public const string JobOutcomeTag = "barako.job.outcome";
    public const string SweepHeldTag = "barako.sweep.held";
    public const string ScheduledTransitionsTag = "barako.scheduler.transitions";
    public const string CollectionSyncIdTag = "barako.collection_sync.id";
    public const string CollectionSyncOutcomeTag = "barako.collection_sync.outcome";
    public const string CollectionSyncCreatedTag = "barako.collection_sync.created";
    public const string CollectionSyncUpdatedTag = "barako.collection_sync.updated";
    public const string CollectionSyncUnchangedTag = "barako.collection_sync.unchanged";
    public const string CollectionSyncSkippedTag = "barako.collection_sync.skipped";
    public const string OutboundScopeTag = "barako.outbound.scope";
    public const string OutboundDestinationTag = "barako.outbound.destination";
    public const string OutboundTriesTag = "barako.outbound.tries";
    public const string OutboundTryTag = "barako.outbound.try";
    public const string OutboundOutcomeTag = "barako.outbound.outcome";
    public const string OutboundDelayTag = "barako.outbound.delay_ms";
    public const string ExceptionTypeTag = "exception.type";

    /// <summary>How a job run ended, beside the outcomes <c>JobMetrics</c> counts.</summary>
    public const string JobGone = "gone";
    public const string JobChanged = "changed";
    public const string JobError = "error";
    public const string JobAbandoned = "abandoned";
    public const string JobReclaimed = "reclaimed";

    public const string OutboundOk = "ok";
    public const string OutboundFailed = "failed";
    public const string OutboundTimeout = "timeout";
    public const string OutboundBreakerOpen = "breaker_open";

    private const int MaxNameLength = 100;

    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>
    /// Starts the span of one workflow action attempt, under the request that caused the run when
    /// the run recorded one. Null when nothing is listening or the trace was not sampled.
    /// </summary>
    /// <remarks>
    /// The runner has no request, so the parent comes from the run: the <c>traceparent</c> of the
    /// span that wrote the triggering event. A run stored before that was kept, or caused by work
    /// with no span, starts a trace of its own.
    /// </remarks>
    public static Activity? StartWorkflowAction(WorkflowRun run, WorkflowActionAttempt attempt, string tenantSlug)
    {
        if (!Source.HasListeners()) return null;

        ActivityContext.TryParse(Correlation.NormaliseTraceParent(run.TraceParent), null, isRemote: true, out var parent);

        var span = Source.StartActivity(WorkflowActionSpan, ActivityKind.Consumer, parent);
        if (span is null) return null;

        span.SetTag(TenantTag, tenantSlug);
        span.SetTag(WorkflowIdTag, run.WorkflowDefinitionId.ToString());
        span.SetTag(WorkflowRunIdTag, run.Id.ToString());
        span.SetTag(WorkflowTriggerTag, Bounded(run.TriggerEvent));
        span.SetTag(WorkflowActionTag, Bounded(attempt.ActionType));
        span.SetTag(WorkflowOrdinalTag, attempt.Ordinal);
        span.SetTag(WorkflowAttemptTag, attempt.Attempts + 1);

        if (Correlation.Normalise(run.CorrelationId) is { } correlationId)
        {
            span.SetTag(CorrelationIdTag, correlationId);
        }

        return span;
    }

    /// <summary>Records how the attempt went. The status only, never the reason.</summary>
    public static void RecordOutcome(Activity? span, AttemptStatus status)
    {
        if (span is null) return;

        span.SetTag(WorkflowOutcomeTag, status.ToString());

        if (status is AttemptStatus.Failed or AttemptStatus.Unknown)
        {
            span.SetStatus(ActivityStatusCode.Error);
        }
    }

    /// <summary>
    /// Starts the span of one job queue poll that found candidates. It is current until disposed,
    /// so the claim's own writes sit under it.
    /// </summary>
    public static Activity? StartJobClaim(string queue)
    {
        if (!Source.HasListeners()) return null;

        var span = Source.StartActivity(JobClaimSpan, ActivityKind.Internal);
        span?.SetTag(JobQueueTag, Bounded(queue));
        return span;
    }

    public static void RecordClaimed(Activity? span, int claimed) => span?.SetTag(JobClaimedTag, claimed);

    /// <summary>
    /// Starts the span of one job run, from its claim to the write that records how it went. Not
    /// made current: the handler runs on the queue's own worker, and the span is ended by whichever
    /// of the two finishing calls the queue makes.
    /// </summary>
    /// <remarks>
    /// The tenant, the job's id, the command's type name and the attempt number. Never the command
    /// itself, which holds the payload a request queued, and never the error a failure stored.
    /// </remarks>
    public static Activity? StartJobRun(JobRecord job, ActivityContext claim)
    {
        if (!Source.HasListeners()) return null;

        var current = Activity.Current;
        var span = Source.StartActivity(JobRunSpan, ActivityKind.Consumer, claim);
        Activity.Current = current;
        if (span is null) return null;

        span.SetTag(TenantTag, Multitenancy.TenantScopes.SlugFor(job.TenantId));
        span.SetTag(JobIdTag, job.TrackingID.ToString());
        span.SetTag(JobCommandTag, Bounded(job.CommandType));
        span.SetTag(JobAttemptTag, job.AttemptCount + 1);
        return span;
    }

    /// <summary>Starts the span of the write that records how a run went, under the run.</summary>
    public static Activity? StartJobFinish(Activity? run)
    {
        if (run is null) return null;

        var span = Source.StartActivity(JobFinishSpan, ActivityKind.Internal, run.Context);
        span?.SetTag(TenantTag, run.GetTagItem(TenantTag));
        span?.SetTag(JobIdTag, run.GetTagItem(JobIdTag));
        return span;
    }

    /// <summary>Ends a run span with its outcome, leaving whatever span is current as it was.</summary>
    public static void EndJobRun(Activity? run, string outcome)
    {
        if (run is null) return;

        run.SetTag(JobOutcomeTag, outcome);
        if (outcome != Jobs.JobMetrics.Succeeded) run.SetStatus(ActivityStatusCode.Error);

        var current = Activity.Current;
        run.Stop();
        Activity.Current = current;
    }

    /// <summary>
    /// Starts the span of one tick of a background sweep. A hosted service runs with no current
    /// span, so this starts a trace of its own.
    /// </summary>
    public static Activity? StartSweep(string name) =>
        Source.HasListeners() ? Source.StartActivity(name, ActivityKind.Internal) : null;

    /// <summary>Starts the span of a sweep's work in one tenant, or of one collection sync.</summary>
    public static Activity? StartTenantWork(string name, string? martenTenantId)
    {
        if (!Source.HasListeners()) return null;

        var span = Source.StartActivity(name, ActivityKind.Internal);
        span?.SetTag(TenantTag, Multitenancy.TenantScopes.SlugFor(martenTenantId));
        return span;
    }

    /// <summary>
    /// Starts the span of one outbound call through the retry and the breaker. Each try is a child,
    /// and the HTTP span of that try a child of the try.
    /// </summary>
    /// <param name="partition">The tenant whose breaker this is, or empty when there is none.</param>
    /// <param name="destination">A host, never a URL.</param>
    public static Activity? StartOutboundCall(string scope, string partition, string destination)
    {
        if (!Source.HasListeners()) return null;

        var span = Source.StartActivity(OutboundCallSpan, ActivityKind.Internal);
        if (span is null) return null;

        span.SetTag(OutboundScopeTag, Bounded(scope));
        span.SetTag(OutboundDestinationTag, Bounded(destination));
        if (!string.IsNullOrEmpty(partition)) span.SetTag(TenantTag, Bounded(partition));
        return span;
    }

    public static Activity? StartOutboundTry(int number)
    {
        if (!Source.HasListeners()) return null;

        var span = Source.StartActivity(OutboundTrySpan, ActivityKind.Internal);
        span?.SetTag(OutboundTryTag, number);
        return span;
    }

    /// <summary>Records how a try or a whole call went: a fixed word, never the error text.</summary>
    public static void RecordOutbound(Activity? span, string outcome, int? tries = null)
    {
        if (span is null) return;

        span.SetTag(OutboundOutcomeTag, outcome);
        if (tries is { } count) span.SetTag(OutboundTriesTag, count);
        if (outcome != OutboundOk) span.SetStatus(ActivityStatusCode.Error);
    }

    /// <summary>
    /// Carom's retry hook. Puts a <see cref="RetryEvent"/> on the outbound call span the retry
    /// belongs to: the number of the try about to run, the wait before it, and the type of what
    /// failed.
    /// </summary>
    /// <remarks>
    /// The hook is process-wide and a module may run Carom for its own calls, so a retry raised
    /// anywhere but directly inside <see cref="StartOutboundCall"/>'s span is ignored. Carom raises
    /// it after the failed try has returned, so the current span is the call.
    /// </remarks>
    public static void RecordRetry(global::Carom.RetrySignal signal)
    {
        if (Activity.Current is not { } span || span.Source != Source || span.OperationName != OutboundCallSpan) return;

        span.AddEvent(new ActivityEvent(RetryEvent, tags: new ActivityTagsCollection
        {
            [OutboundTryTag] = signal.Attempt + 1,
            [OutboundDelayTag] = (long)signal.Delay.TotalMilliseconds,
            [ExceptionTypeTag] = Bounded(signal.ExceptionTypeName ?? "result"),
        }));
    }

    /// <summary>A stored name, cut to a length a span attribute can carry. Null, which only damaged data holds, is empty.</summary>
    private static string Bounded(string? value) =>
        value is null ? string.Empty
        : value.Length <= MaxNameLength ? value : value[..MaxNameLength];
}
