using System.Collections.Concurrent;
using barakoCMS.Models;
using Prometheus;

namespace barakoCMS.Features.Workflows;

/// <summary>What the workflow queue and its runner publish on <c>/metrics</c>.</summary>
/// <remarks>
/// A label value is one of the fixed words in this file or an action type a handler is registered
/// for. Never a tenant, a workflow, a run, a content type or an error. <c>/metrics</c> is read for
/// the whole deployment by whoever holds the scrape key, so a value a tenant chose would show that
/// tenant's data to the reader, and would let a tenant add series without limit.
///
/// One instance per registry. The host uses <see cref="Default"/>. A test builds its own on a
/// registry nothing else writes to.
/// </remarks>
internal sealed class WorkflowMetrics
{
    /// <summary>The label value for anything that is not in the fixed set.</summary>
    public const string Other = "other";

    /// <summary>
    /// How many action types get a label value of their own. Types past that are counted as
    /// <see cref="Other"/>.
    /// </summary>
    /// <remarks>
    /// The registered set is whatever the host and its modules add, so it is small but not known
    /// here. This is what makes the number of series a number and not a habit.
    /// </remarks>
    public const int MaxActionLabels = 50;

    // Declared before Default, which reads it: static fields are set in the order they are written.
    private static readonly double[] DurationBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300];

    public static readonly WorkflowMetrics Default = new(Prometheus.Metrics.DefaultRegistry);

    private readonly ConcurrentDictionary<string, int> _actionLabels = new(StringComparer.Ordinal);
    private int _actionLabelsGiven;

    public WorkflowMetrics(CollectorRegistry registry)
    {
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);

        RunsQueued = factory.CreateCounter(
            "barakocms_workflow_runs_queued_total",
            "Workflow runs queued, by the kind of event that fired them.",
            new CounterConfiguration { LabelNames = ["trigger"] });

        AttemptsClaimed = factory.CreateCounter(
            "barakocms_workflow_attempts_claimed_total",
            "Workflow action attempts this node's runner claimed.");

        Attempts = factory.CreateCounter(
            "barakocms_workflow_attempts_total",
            "Workflow action attempts whose outcome this node's runner recorded, by action type and outcome.",
            new CounterConfiguration { LabelNames = ["action", "outcome"] });

        ActionDuration = factory.CreateHistogram(
            "barakocms_workflow_action_duration_seconds",
            "How long a workflow action took, for attempts whose outcome was recorded, by action type.",
            new HistogramConfiguration { LabelNames = ["action"], Buckets = DurationBuckets });

        RunsFinished = factory.CreateCounter(
            "barakocms_workflow_runs_finished_total",
            "Workflow runs this node's runner finished, by the status they ended in.",
            new CounterConfiguration { LabelNames = ["status"] });

        RunsHalted = factory.CreateCounter(
            "barakocms_workflow_runs_halted_total",
            "Workflow runs where an action set to halt failed and the actions after it were skipped.");

        LastPass = factory.CreateGauge(
            "barakocms_workflow_runner_last_pass_timestamp_seconds",
            "Unix time this node's workflow runner last completed a pass.",
            new GaugeConfiguration { SuppressInitialValue = true });

        DueRuns = factory.CreateGauge(
            "barakocms_workflow_due_runs",
            "Workflow runs with an action that could be claimed now, as of the last measurement.",
            new GaugeConfiguration { SuppressInitialValue = true });

        OldestDueRunAge = factory.CreateGauge(
            "barakocms_workflow_oldest_due_run_age_seconds",
            "Seconds since the oldest workflow run that is due was queued, as of the last measurement. Zero when none is due.",
            new GaugeConfiguration { SuppressInitialValue = true });

        BacklogMeasured = factory.CreateGauge(
            "barakocms_workflow_backlog_measured_timestamp_seconds",
            "Unix time the due runs were last counted by this node.",
            new GaugeConfiguration { SuppressInitialValue = true });
    }

    public Counter RunsQueued { get; }

    public Counter AttemptsClaimed { get; }

    public Counter Attempts { get; }

    public Histogram ActionDuration { get; }

    public Counter RunsFinished { get; }

    public Counter RunsHalted { get; }

    public Gauge LastPass { get; }

    public Gauge DueRuns { get; }

    public Gauge OldestDueRunAge { get; }

    public Gauge BacklogMeasured { get; }

    public void Queued(string trigger, int runs)
    {
        if (runs > 0) RunsQueued.WithLabels(TriggerLabel(trigger)).Inc(runs);
    }

    public void Claimed() => AttemptsClaimed.Inc();

    /// <summary>Counts one attempt whose outcome has been saved.</summary>
    /// <param name="registered">Whether a handler is registered for the type.</param>
    /// <param name="status">
    /// The attempt's status as saved. Pending is an attempt that failed and was queued again.
    /// </param>
    public void Recorded(string actionType, bool registered, AttemptStatus status, long elapsedMs)
    {
        var action = ActionLabel(actionType, registered);

        Attempts.WithLabels(action, OutcomeLabel(status)).Inc();

        // Skipped is the content having gone, and an unregistered type has no handler. Neither
        // called anything, and their zeroes would read as a fast provider.
        if (registered && status != AttemptStatus.Skipped)
        {
            ActionDuration.WithLabels(action).Observe(elapsedMs / 1000d);
        }
    }

    /// <summary>Counts a run that has just ended. A run that is still open is not counted.</summary>
    public void Finished(RunStatus status)
    {
        if (StatusLabel(status) is { } label) RunsFinished.WithLabels(label).Inc();
    }

    public void Halted() => RunsHalted.Inc();

    /// <summary>
    /// Whether this attempt, as just recorded, halted its run: it ended Failed or Unknown and an
    /// action after it was skipped because of it.
    /// </summary>
    /// <remarks>
    /// Read from the run after <see cref="WorkflowRun.HaltAfter"/> has been applied. A halting
    /// action that failed with nothing left behind it stopped nothing and is not counted.
    /// </remarks>
    internal static bool HaltedTheRun(WorkflowRun run, WorkflowActionAttempt attempt) =>
        attempt.Status is AttemptStatus.Failed or AttemptStatus.Unknown
        && run.Actions.Any(a => a.Status == AttemptStatus.Skipped && a.HaltedBy == attempt.Ordinal);

    public void PassCompleted(DateTimeOffset now) => LastPass.Set(UnixSeconds(now));

    public void Backlog(int due, TimeSpan oldest, DateTimeOffset now)
    {
        DueRuns.Set(due);
        OldestDueRunAge.Set(Math.Max(oldest.TotalSeconds, 0));
        BacklogMeasured.Set(UnixSeconds(now));
    }

    /// <summary>
    /// The type itself for the first <see cref="MaxActionLabels"/> registered types seen, and
    /// <see cref="Other"/> for the rest and for a type nothing is registered for.
    /// </summary>
    /// <remarks>
    /// A run's action type is whatever was stored on the workflow, so it is only used as a label
    /// once a handler has matched it. Two threads seeing a new type together can each take a
    /// number, which gives fewer labelled types than the limit and never more.
    /// </remarks>
    internal string ActionLabel(string actionType, bool registered)
    {
        if (!registered || string.IsNullOrWhiteSpace(actionType)) return Other;

        var given = _actionLabels.GetOrAdd(actionType, _ => Interlocked.Increment(ref _actionLabelsGiven));
        return given <= MaxActionLabels ? actionType : Other;
    }

    /// <summary>
    /// The trigger as a label. A transition is named by a tenant, so every transition is counted as
    /// one value.
    /// </summary>
    internal static string TriggerLabel(string trigger) => trigger switch
    {
        WorkflowEvents.Created => "created",
        WorkflowEvents.Updated => "updated",
        WorkflowEvents.Deleted => "deleted",
        WorkflowEvents.Published => "published",
        WorkflowEvents.Unpublished => "unpublished",
        _ when WorkflowEvents.IsTransition(trigger) => "transition",
        _ => Other,
    };

    internal static string OutcomeLabel(AttemptStatus status) => status switch
    {
        AttemptStatus.Succeeded => "succeeded",
        AttemptStatus.Failed => "failed",
        AttemptStatus.Pending => "retried",
        AttemptStatus.Unknown => "unknown",
        AttemptStatus.Skipped => "skipped",
        _ => Other,
    };

    internal static string? StatusLabel(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "succeeded",
        RunStatus.Failed => "failed",
        RunStatus.PartiallyFailed => "partially_failed",
        RunStatus.Cancelled => "cancelled",
        _ => null,
    };

    private static double UnixSeconds(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds() / 1000d;
}
