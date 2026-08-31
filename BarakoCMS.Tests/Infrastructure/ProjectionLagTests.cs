using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using barakoCMS.Infrastructure.Health;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// What the workflow projection's progress means for health.
/// </summary>
/// <remarks>
/// A pure function so the case that matters, a shard that stopped, can be asserted without stalling
/// a real daemon. The integration side of this lives in
/// <see cref="BarakoCMS.Tests.Features.Workflows.WorkflowHealthCheckTests"/>, which proves the check
/// is actually wired into the health report: a rule nobody registered passes its own unit tests.
/// </remarks>
public class ProjectionLagTests
{
    [Fact]
    public void A_projection_with_no_progress_row_is_degraded_once_events_exist()
    {
        var reading = ProjectionLag.Evaluate("WorkflowProjection", eventCount: 42, highWaterMark: 42, shardSequence: null);

        reading.Status.Should().Be(HealthStatus.Degraded,
            "no progress row while events exist means the daemon has not processed this projection, "
          + "which is what a stopped shard looks like");
        reading.Lag.Should().BeNull("how far behind it is cannot be known without a progress row");
        reading.Description.Should().Contain("has no recorded progress");
    }

    [Fact]
    public void An_empty_event_store_is_healthy()
    {
        // A fresh store reports a sequence number of 1 with no events in it, which is why emptiness
        // is decided on the count rather than the high-water mark.
        var reading = ProjectionLag.Evaluate("WorkflowProjection", eventCount: 0, highWaterMark: 1, shardSequence: null);

        reading.Status.Should().Be(HealthStatus.Healthy,
            "a fresh install has nothing to process, and reporting a fault there would train everyone "
          + "to ignore this check");
        reading.Lag.Should().Be(0);
    }

    [Fact]
    public void A_projection_within_the_tolerance_is_healthy()
    {
        var reading = ProjectionLag.Evaluate("WorkflowProjection", eventCount: 1_000, highWaterMark: 1_000, shardSequence: 990, tolerance: 100);

        reading.Status.Should().Be(HealthStatus.Healthy);
        reading.Lag.Should().Be(10);
    }

    [Fact]
    public void A_projection_past_the_tolerance_is_degraded()
    {
        var reading = ProjectionLag.Evaluate("WorkflowProjection", eventCount: 1_000, highWaterMark: 1_000, shardSequence: 500, tolerance: 100);

        reading.Status.Should().Be(HealthStatus.Degraded);
        reading.Lag.Should().Be(500);
        reading.Description.Should().Contain("500");
    }

    [Fact]
    public void A_caught_up_projection_reports_no_lag()
    {
        var reading = ProjectionLag.Evaluate("WorkflowProjection", eventCount: 1_000, highWaterMark: 1_000, shardSequence: 1_000);

        reading.Status.Should().Be(HealthStatus.Healthy);
        reading.Lag.Should().Be(0);
    }

    [Fact]
    public void Nothing_reports_Unhealthy()
    {
        // /health is what the liveness probe reads, and restarting the pod does not un-stop a shard:
        // the new one resumes at the same sequence and fails on the same event. Degraded is visible
        // without putting the deployment into a restart loop.
        var readings = new[]
        {
            ProjectionLag.Evaluate("WorkflowProjection", 1_000_000, 1_000_000, null),
            ProjectionLag.Evaluate("WorkflowProjection", 1_000_000, 1_000_000, 0),
            ProjectionLag.Evaluate("WorkflowProjection", 0, 1, null),
        };

        readings.Should().OnlyContain(r => r.Status != HealthStatus.Unhealthy);
    }
}
