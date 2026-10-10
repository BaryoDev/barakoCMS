using System.Collections.Concurrent;
using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Jobs;

/// <summary>
/// The run spans of jobs this node has claimed and not yet finished, so the call that records a
/// job's outcome can end the span its claim started.
/// </summary>
/// <remarks>
/// Bounded. A job the queue claims and then never finishes (the worker stopped, the lease ran out
/// mid-run) would otherwise hold its span for ever, so every claim first ends the spans older than
/// twice the lease as <see cref="BarakoTracing.JobAbandoned"/>, and past <see cref="MaxOpen"/> a new
/// run gets no span at all rather than growing the table. Empty, and never touched, when nothing
/// listens for spans.
/// </remarks>
internal sealed class JobRunSpans
{
    public const int MaxOpen = 10_000;

    private readonly ConcurrentDictionary<Guid, Activity> _open = new();

    public int Count => _open.Count;

    public void Started(JobRecord job, ActivityContext claim)
    {
        if (_open.Count >= MaxOpen) return;

        var span = BarakoTracing.StartJobRun(job, claim);
        if (span is null) return;

        // The same job claimed again while its span is open: its lease ran out and this node took
        // it back. The earlier run is over whatever it is doing.
        if (_open.TryRemove(job.TrackingID, out var earlier))
        {
            BarakoTracing.EndJobRun(earlier, BarakoTracing.JobReclaimed);
        }

        _open[job.TrackingID] = span;
    }

    /// <summary>The open run span of <paramref name="trackingId"/>, removed, or null when there is none.</summary>
    public Activity? Take(Guid trackingId) => _open.TryRemove(trackingId, out var span) ? span : null;

    /// <summary>Puts back a span <see cref="Take"/> handed out, when the write it was taken for failed.</summary>
    /// <remarks>
    /// If the job was claimed again in between, the new run is the one that stays open, and the
    /// span put back is ended as <see cref="BarakoTracing.JobReclaimed"/>.
    /// </remarks>
    public void Restore(Guid trackingId, Activity? span)
    {
        if (span is null) return;

        if (!_open.TryAdd(trackingId, span))
        {
            BarakoTracing.EndJobRun(span, BarakoTracing.JobReclaimed);
        }
    }

    public void EndStartedBefore(DateTime cutoffUtc)
    {
        foreach (var (id, span) in _open)
        {
            if (span.StartTimeUtc < cutoffUtc && _open.TryRemove(KeyValuePair.Create(id, span)))
            {
                BarakoTracing.EndJobRun(span, BarakoTracing.JobAbandoned);
            }
        }
    }
}
