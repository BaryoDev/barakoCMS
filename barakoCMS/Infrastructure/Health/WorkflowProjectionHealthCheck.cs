using Marten;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace barakoCMS.Infrastructure.Health;

/// <summary>
/// Reports how far the workflow projection is behind the event stream.
/// </summary>
/// <remarks>
/// The workflow projection runs in Marten's async daemon, and an unhandled exception there stops the
/// shard. Every workflow then silently stops firing: no email, no webhook, no task, and no ongoing
/// signal in the logs after the first exception. Database, disk and memory checks all stay green
/// through that, which is why this one exists. See docs/operating-workflows.md.
/// </remarks>
internal sealed class WorkflowProjectionHealthCheck : IHealthCheck
{
    public const string Name = "Workflow Projection";

    /// <summary>Short name, for the gauge label and the health description.</summary>
    public const string ProjectionName = nameof(Features.Workflows.WorkflowProjection);

    // Marten names the shard after the projection's FULL type name, as in
    // "barakoCMS.Features.Workflows.WorkflowProjection:All". Taken from the type rather than written
    // out, so moving or renaming the projection cannot leave this silently matching nothing, which
    // reads exactly like the stalled shard the check exists to find.
    private static readonly string ShardPrefix = typeof(Features.Workflows.WorkflowProjection).FullName!;

    private static readonly Gauge LagGauge = Metrics.CreateGauge(
        "barakocms_projection_lag_events",
        "Events appended but not yet processed by an async projection shard.",
        new GaugeConfiguration { LabelNames = ["projection"] });

    private readonly IDocumentStore _store;
    private readonly long _tolerance;

    public WorkflowProjectionHealthCheck(IDocumentStore store, long tolerance = ProjectionLag.DefaultTolerance)
    {
        _store = store;
        _tolerance = tolerance;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statistics = await _store.Advanced.FetchEventStoreStatistics(token: cancellationToken);
        var progress = await _store.Advanced.AllProjectionProgress(token: cancellationToken);

        var shard = progress.FirstOrDefault(p =>
            p.ShardName.StartsWith(ShardPrefix, StringComparison.OrdinalIgnoreCase));

        var reading = ProjectionLag.Evaluate(
            ProjectionName, statistics.EventCount, statistics.EventSequenceNumber, shard?.Sequence, _tolerance);

        // Refreshed whenever health is evaluated, which the shipped probes do every ten seconds.
        // Nothing else polls it, so a deployment with no health probe gets a stale gauge.
        if (reading.Lag is { } lag)
        {
            LagGauge.WithLabels(ProjectionName).Set(lag);
        }

        return new HealthCheckResult(reading.Status, reading.Description, data: new Dictionary<string, object>
        {
            ["projection"] = ProjectionName,
            ["eventCount"] = statistics.EventCount,
            ["highWaterMark"] = statistics.EventSequenceNumber,
            ["shardSequence"] = shard?.Sequence ?? -1,
            ["lagEvents"] = reading.Lag ?? -1,
        });
    }
}
