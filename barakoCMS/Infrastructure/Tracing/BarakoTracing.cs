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

    private static string Bounded(string value) =>
        value.Length <= MaxNameLength ? value : value[..MaxNameLength];
}
