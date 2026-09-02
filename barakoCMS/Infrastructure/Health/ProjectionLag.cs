using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace barakoCMS.Infrastructure.Health;

/// <summary>How far behind the event stream an async projection is, and what that means.</summary>
/// <param name="Status">Health status to report.</param>
/// <param name="Lag">Events appended but not yet processed by the shard, or null when unknown.</param>
/// <param name="Description">What an operator reading the health dashboard needs to know.</param>
internal readonly record struct ProjectionLagReading(HealthStatus Status, long? Lag, string Description);

/// <summary>
/// Decides what a projection's progress means, given the high-water mark and the shard's sequence.
/// </summary>
/// <remarks>
/// A pure function so the interesting cases (a shard that has never run, a shard that has stopped)
/// can be tested without stalling a real daemon.
///
/// Nothing here reports Unhealthy. The shipped probes point liveness at <c>/health</c>, so an
/// Unhealthy result restarts the pod, and restarting is not a remedy for a halted shard: the new
/// pod resumes at the same sequence and fails on the same event. Degraded is visible on the admin
/// health page and in the lag gauge, and does not put the deployment into a restart loop.
/// </remarks>
internal static class ProjectionLag
{
    /// <summary>Events behind before the projection is called degraded.</summary>
    public const long DefaultTolerance = 1000;

    /// <param name="projectionName">Projection the reading is about.</param>
    /// <param name="eventCount">
    /// Events in the store. Read separately from the high-water mark because a fresh store reports a
    /// sequence number of 1 with nothing in it, and calling that a stalled projection would report a
    /// fault on every new install.
    /// </param>
    /// <param name="highWaterMark">Sequence of the newest appended event.</param>
    /// <param name="shardSequence">Sequence the shard has reached, or null if it has no progress row.</param>
    /// <param name="tolerance">Events behind before this is called degraded.</param>
    public static ProjectionLagReading Evaluate(
        string projectionName,
        long eventCount,
        long highWaterMark,
        long? shardSequence,
        long tolerance = DefaultTolerance)
    {
        if (eventCount <= 0)
        {
            return new ProjectionLagReading(HealthStatus.Healthy, 0,
                $"{projectionName} has nothing to process: no events have been appended.");
        }

        if (shardSequence is null)
        {
            // Distinct from "behind": there is no progress row at all, so the daemon has not run
            // this projection since the events were written. On a node that has just started this
            // clears on its own within a few seconds; if it persists, the shard is not running.
            return new ProjectionLagReading(HealthStatus.Degraded, null,
                $"{projectionName} has no recorded progress while {highWaterMark} event(s) exist. "
                + "The projection daemon has not processed this projection.");
        }

        var lag = highWaterMark - shardSequence.Value;
        if (lag < 0)
        {
            lag = 0;
        }

        return lag > tolerance
            ? new ProjectionLagReading(HealthStatus.Degraded, lag,
                $"{projectionName} is {lag} event(s) behind, over the tolerance of {tolerance}.")
            : new ProjectionLagReading(HealthStatus.Healthy, lag,
                $"{projectionName} is {lag} event(s) behind.");
    }
}
