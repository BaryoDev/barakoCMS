using Prometheus;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>What the job queue publishes on <c>/metrics</c>.</summary>
/// <remarks>
/// Label values are the fixed words in this file. Never a queue, a command, a tenant or an error,
/// for the reason <c>WorkflowMetrics</c> gives: <c>/metrics</c> is read for the whole deployment.
///
/// One instance per registry. The host uses <see cref="Default"/>. A test builds its own on a
/// registry nothing else writes to.
/// </remarks>
internal sealed class JobMetrics
{
    public const string Succeeded = "succeeded";
    public const string Retried = "retried";
    public const string DeadLettered = "dead_lettered";

    public static readonly JobMetrics Default = new(Prometheus.Metrics.DefaultRegistry);

    public JobMetrics(CollectorRegistry registry)
    {
        var factory = Prometheus.Metrics.WithCustomRegistry(registry);

        Attempts = factory.CreateCounter(
            "barakocms_jobs_attempts_total",
            "Job attempts whose outcome this node recorded: succeeded, retried, or dead_lettered when it was the last allowed.",
            new CounterConfiguration { LabelNames = ["outcome"] });

        Due = factory.CreateGauge(
            "barakocms_jobs_due",
            "Jobs a worker could claim now, as of the last measurement.",
            new GaugeConfiguration { SuppressInitialValue = true });

        OldestDueAge = factory.CreateGauge(
            "barakocms_jobs_oldest_due_age_seconds",
            "Seconds the oldest due job has waited past the time it was due, as of the last measurement. Zero when none is due.",
            new GaugeConfiguration { SuppressInitialValue = true });

        DeadLetteredJobs = factory.CreateGauge(
            "barakocms_jobs_dead_lettered",
            "Dead-lettered and cancelled jobs still stored, as of the last measurement.",
            new GaugeConfiguration { SuppressInitialValue = true });
    }

    public Counter Attempts { get; }

    public Gauge Due { get; }

    public Gauge OldestDueAge { get; }

    public Gauge DeadLetteredJobs { get; }

    public void Recorded(string outcome) => Attempts.WithLabels(outcome).Inc();

    public void Queue(int due, TimeSpan oldestDue, int deadLettered)
    {
        Due.Set(due);
        OldestDueAge.Set(Math.Max(oldestDue.TotalSeconds, 0));
        DeadLetteredJobs.Set(deadLettered);
    }
}
