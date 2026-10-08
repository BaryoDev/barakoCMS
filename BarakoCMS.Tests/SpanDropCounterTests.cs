using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: a span the scrubber keeps from export is counted, by reason, since it leaves nothing
/// in the trace to show it was there.
/// </summary>
/// <remarks>
/// The counter is process-wide and other tests end spans too, so each case asserts that its own
/// span was dropped and that at least one measurement with its reason arrived meanwhile.
/// </remarks>
public class SpanDropCounterTests
{
    [Fact]
    public void A_database_span_with_no_parent_is_dropped_and_counted_as_parentless()
    {
        using var counted = new Counted();
        using var source = new ActivitySource(SpanScrubber.DatabaseSource);
        using var listener = Listen(SpanScrubber.DatabaseSource);

        var previous = Activity.Current;
        Activity.Current = null;
        var span = source.StartActivity("postgres", ActivityKind.Client);
        Activity.Current = previous;

        span.Should().NotBeNull();
        span!.Stop();
        using var scrubber = new SpanScrubber();
        scrubber.OnEnd(span);

        span.Recorded.Should().BeFalse("an unrecorded span is one the exporters skip");
        counted.Reasons.Should().Contain(SpanScrubber.Parentless);
    }

    [Fact]
    public void A_cut_down_span_with_an_event_holding_attributes_is_dropped_and_counted()
    {
        using var counted = new Counted();
        var sourceName = "BarakoCMS.Tests.SpanDrop." + Guid.NewGuid().ToString("N");
        using var source = new ActivitySource(sourceName);
        using var listener = Listen(sourceName);

        var span = source.StartActivity("an outbound call", ActivityKind.Client);
        span.Should().NotBeNull();
        span!.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.message"] = "the provider quoted what it was sent",
        }));
        span.Stop();
        using var scrubber = new SpanScrubber();
        scrubber.OnEnd(span);

        span.Recorded.Should().BeFalse();
        counted.Reasons.Should().Contain(SpanScrubber.WithEvent);
    }

    private static ActivityListener Listen(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Hears the dropped-span counter until disposed and keeps each measurement's reason.</summary>
    private sealed class Counted : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<string> _reasons = new();

        public Counted()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SpanScrubber.MeterName && instrument.Name == SpanScrubber.DroppedCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value is string reason) _reasons.Enqueue(reason);
                }
            });
            _listener.Start();
        }

        public IReadOnlyCollection<string> Reasons => _reasons;

        public void Dispose() => _listener.Dispose();
    }
}
