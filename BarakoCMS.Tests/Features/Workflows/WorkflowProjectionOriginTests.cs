using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #691: the projection runs in the daemon, with no request, so what it queues takes its
/// origin from the event: the correlation id and the traceparent the event was stored with.
/// </summary>
/// <remarks>
/// The queue is a substitute that records the ambient origin at the moment it is called, which is
/// the moment <c>WorkflowRunQueue</c> reads it to fill a new run.
/// </remarks>
public class WorkflowProjectionOriginTests
{
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();
    private readonly IDocumentOperations _ops = Substitute.For<IDocumentOperations>();
    private readonly IWorkflowRunQueue _queue = Substitute.For<IWorkflowRunQueue>();
    private readonly Guid _contentId = Guid.NewGuid();

    private int _calls;
    private string? _seenId;
    private string? _seenCause;

    public WorkflowProjectionOriginTests()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();

        _services.GetService(typeof(IServiceScopeFactory)).Returns(scopes);
        _services.GetService(typeof(TenantContext)).Returns(new TenantContext());
        _services.GetService(typeof(IWorkflowRunQueue)).Returns(_queue);
        scopes.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(_services);

        _ops.LoadAsync<Content>(_contentId, Arg.Any<CancellationToken>())
            .Returns(new Content { Id = _contentId, ContentType = "Article", Data = new Dictionary<string, object>() });

        _queue.EnqueueAsync(Arg.Any<Content>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls++;
                _seenId = Correlation.Id;
                _seenCause = Correlation.Cause;
                return 1;
            });
    }

    [Fact]
    public async Task An_updated_event_hands_its_origin_to_what_is_queued()
    {
        var e = Envelope(new barakoCMS.Events.ContentUpdated(
            _contentId, new Dictionary<string, object>(), Guid.NewGuid(), null, DateTime.UtcNow), "req-updated", TraceParent);

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1, "the event was queued, or there is nothing to assert about");
        _seenId.Should().Be("req-updated");
        _seenCause.Should().Be(TraceParent);
    }

    [Fact]
    public async Task A_created_event_hands_its_origin_to_what_is_queued()
    {
        var e = Envelope(new barakoCMS.Events.ContentCreated(
            _contentId, "Article", new Dictionary<string, object>(), ContentStatus.Draft, Guid.NewGuid(), null,
            SensitivityLevel.Public, DateTime.UtcNow), "req-created", TraceParent);

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1);
        _seenId.Should().Be("req-created");
        _seenCause.Should().Be(TraceParent);
    }

    [Fact]
    public async Task A_publish_hands_its_origin_to_what_is_queued()
    {
        var e = Envelope(new barakoCMS.Events.ContentStatusChanged(
            _contentId, ContentStatus.Published, Guid.NewGuid(), DateTime.UtcNow), "req-published", TraceParent);

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1);
        _seenId.Should().Be("req-published");
        _seenCause.Should().Be(TraceParent);
    }

    [Fact]
    public async Task A_transition_hands_its_origin_to_what_is_queued()
    {
        var e = Envelope(new barakoCMS.Events.ContentTransitioned(
            _contentId, "approve", "Submitted", "Approved", Guid.NewGuid(), DateTime.UtcNow), "req-transition", TraceParent);

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1);
        _seenId.Should().Be("req-transition");
        _seenCause.Should().Be(TraceParent);
    }

    /// <remarks>
    /// Every event stored before 4.6, and every event no request caused. The run is still queued,
    /// with no origin, and the current span is not borrowed as one.
    /// </remarks>
    [Fact]
    public async Task An_event_with_no_stored_origin_is_still_queued_and_with_none()
    {
        var e = Envelope(new barakoCMS.Events.ContentUpdated(
            _contentId, new Dictionary<string, object>(), Guid.NewGuid(), null, DateTime.UtcNow), null, null);

        using var daemonSpan = new System.Diagnostics.Activity("a span of the daemon");
        daemonSpan.SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C);
        daemonSpan.Start();

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1, "an event with no metadata fires its workflows as it always did");
        _seenId.Should().BeNull();
        _seenCause.Should().BeNull("the daemon's span did not cause the run");
    }

    [Fact]
    public async Task Stored_metadata_that_is_not_an_id_is_not_passed_on()
    {
        var e = Envelope(new barakoCMS.Events.ContentUpdated(
            _contentId, new Dictionary<string, object>(), Guid.NewGuid(), null, DateTime.UtcNow),
            "two words\nand a line", "00-not-a-traceparent");

        await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

        _calls.Should().Be(1);
        _seenId.Should().BeNull();
        _seenCause.Should().BeNull();
    }

    /// <remarks>
    /// Asserted from inside the call, where the queue reads it. Whatever was ambient when the
    /// daemon reached this event, another event's request or a scope of the host's, is not what
    /// caused this run.
    /// </remarks>
    [Fact]
    public async Task An_events_origin_replaces_whatever_was_ambient_when_the_daemon_reached_it()
    {
        var e = Envelope(new barakoCMS.Events.ContentUpdated(
            _contentId, new Dictionary<string, object>(), Guid.NewGuid(), null, DateTime.UtcNow), "req-this-event", TraceParent);

        using (Correlation.Begin("some-other-request"))
        {
            await new WorkflowProjection(_services).Project(e, _ops, CancellationToken.None);

            _calls.Should().Be(1);
            _seenId.Should().Be("req-this-event");
            _seenCause.Should().Be(TraceParent);
        }
    }

    private IEvent<T> Envelope<T>(T data, string? correlationId, string? causationId) where T : notnull
    {
        var e = Substitute.For<IEvent<T>>();
        e.Data.Returns(data);
        e.TenantId.Returns(JasperFx.StorageConstants.DefaultTenantId);
        e.Sequence.Returns(42L);
        e.CorrelationId.Returns(correlationId);
        e.CausationId.Returns(causationId);
        return e;
    }
}
