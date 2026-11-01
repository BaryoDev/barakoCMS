using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Tracing;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The sweep replays a missed trigger from the event as it was stored, so these tests store real
/// events and then queue a row for the one under test.
/// </summary>
/// <remarks>
/// The workflow is stored after the events. The host's own projection queues a run for an event
/// the moment it is appended when a workflow already listens, which would leave nothing for the
/// sweep to do and nothing for the test to prove.
/// </remarks>
[Collection("Integration1")]
public class WorkflowMissedTriggerReplayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IntegrationTestFixture _fixture;

    public WorkflowMissedTriggerReplayTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task An_updated_trigger_matches_the_data_the_event_wrote_not_the_entry_as_it_is_now()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft), Updated(id, "B"), Updated(id, "C"));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Updated, ("Stage", "B"));

        await QueueRowAsync(tenant, WorkflowEvents.Updated, id, events[1].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        var runs = await RunsAsync(tenant, id);
        runs.Should().HaveCount(1, "the entry reads C now, and the event said B");
        runs[0].TriggeringEventSequence.Should().Be(events[1].Sequence);
        runs[0].TriggerEvent.Should().Be(WorkflowEvents.Updated);
    }

    [Fact]
    public async Task A_created_trigger_matches_the_data_and_status_the_event_wrote()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft), Updated(id, "B"));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Created, ("Stage", "A"), ("Status", "Draft"));

        await QueueRowAsync(tenant, WorkflowEvents.Created, id, events[0].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        var runs = await RunsAsync(tenant, id);
        runs.Should().HaveCount(1);
        runs[0].TriggeringEventSequence.Should().Be(events[0].Sequence);
    }

    [Fact]
    public async Task A_run_made_by_a_replay_carries_the_correlation_id_and_trace_parent_of_the_event()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var correlation = $"replay-{Guid.NewGuid():N}";

        IReadOnlyList<IEvent> events;
        using (var request = new System.Diagnostics.Activity("request under test"))
        {
            request.Start();
            using (Correlation.Begin(correlation))
            {
                events = await AppendAsync(tenant, id, type, Created(id, type, "A", ContentStatus.Draft), Updated(id, "B"));
            }
        }

        events[1].CorrelationId.Should().Be(correlation, "the event was stamped when it was stored");
        events[1].CausationId.Should().NotBeNullOrEmpty();
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Updated);

        await QueueRowAsync(tenant, WorkflowEvents.Updated, id, events[1].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        var run = (await RunsAsync(tenant, id)).Should().ContainSingle().Which;
        run.CorrelationId.Should().Be(correlation);
        run.TraceParent.Should().Be(events[1].CausationId);
    }

    [Fact]
    public async Task A_transition_trigger_is_replayed_under_the_transitions_event_name()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft),
            new ContentTransitioned(id, "approve", "Draft", "Approved", Guid.NewGuid(), DateTime.UtcNow));
        var name = WorkflowEvents.ForTransition("approve");
        await StoreWorkflowAsync(tenant, type, name);

        await QueueRowAsync(tenant, name, id, events[1].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        var runs = await RunsAsync(tenant, id);
        runs.Should().HaveCount(1);
        runs[0].TriggerEvent.Should().Be(name);
    }

    [Fact]
    public async Task An_unpublished_trigger_of_a_published_entry_is_replayed()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft), Status(id, ContentStatus.Published), Status(id, ContentStatus.Archived));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Unpublished);

        await QueueRowAsync(tenant, WorkflowEvents.Unpublished, id, events[2].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        var runs = await RunsAsync(tenant, id);
        runs.Should().HaveCount(1);
        runs[0].TriggeringEventSequence.Should().Be(events[2].Sequence);
    }

    [Fact]
    public async Task An_unpublished_trigger_is_dropped_when_no_workflow_listens_for_it()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft), Status(id, ContentStatus.Published), Status(id, ContentStatus.Archived));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Published);

        await QueueRowAsync(tenant, WorkflowEvents.Unpublished, id, events[2].Sequence);
        (await SweepAsync(tenant)).Should().Be(1, "the row is handled and removed");

        (await RunsAsync(tenant, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unpublished_trigger_of_a_draft_that_was_archived_makes_no_run()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type,
            Created(id, type, "A", ContentStatus.Draft), Status(id, ContentStatus.Archived));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Unpublished);

        await QueueRowAsync(tenant, WorkflowEvents.Unpublished, id, events[1].Sequence);
        (await SweepAsync(tenant)).Should().Be(1);

        (await RunsAsync(tenant, id)).Should().BeEmpty("it was never published, so it was not unpublished");
    }

    [Fact]
    public async Task Two_writers_enqueuing_the_same_event_make_one_run()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type, Created(id, type, "A", ContentStatus.Draft));
        await StoreWorkflowAsync(tenant, type, WorkflowEvents.Created);
        var content = await LoadAsync(tenant, id);

        // Neither save starts until both writers have read that no run exists.
        using var barrier = new Barrier(2);
        var reached = 0;

        async Task<int> WriterAsync()
        {
            var gate = new BarrierListener(barrier, () => Interlocked.Increment(ref reached));
            await using var session = Store.LightweightSession(tenant);
            session.Listeners.Add(gate);
            return await new WorkflowRunQueue(session, NullLogger<WorkflowRunQueue>.Instance)
                .EnqueueAsync(content, WorkflowEvents.Created, events[0].Sequence, Ct);
        }

        var queued = await Task.WhenAll(Task.Run(WriterAsync, Ct), Task.Run(WriterAsync, Ct));

        reached.Should().Be(2, "both writers got as far as the save with the same view of the table");
        (await RunsAsync(tenant, id)).Should().HaveCount(1);
        queued.Sum().Should().Be(1);
    }

    [Fact]
    public async Task A_run_another_writer_inserted_first_does_not_cost_the_other_workflows_their_run()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var (id, type) = NewEntry();
        var events = await AppendAsync(tenant, id, type, Created(id, type, "A", ContentStatus.Draft));
        var first = await StoreWorkflowAsync(tenant, type, WorkflowEvents.Created);
        var second = await StoreWorkflowAsync(tenant, type, WorkflowEvents.Created);
        var content = await LoadAsync(tenant, id);

        // A run under the id the event would get, at a sequence the existence check does not look
        // for, so the check passes and the insert is what collides.
        await using (var seed = Store.LightweightSession(tenant))
        {
            seed.Store(WorkflowRunQueue.NewRun(first, id, type, WorkflowEvents.Created, -1,
                WorkflowRunQueue.RunIdFor(first.Id, id, events[0].Sequence)));
            await seed.SaveChangesAsync(Ct);
        }

        await using var session = Store.LightweightSession(tenant);
        var queued = await new WorkflowRunQueue(session, NullLogger<WorkflowRunQueue>.Instance)
            .EnqueueAsync(content, WorkflowEvents.Created, events[0].Sequence, Ct);

        queued.Should().Be(1, "only the second workflow's run was new");
        var runs = await RunsAsync(tenant, id);
        runs.Should().HaveCount(2);
        runs.Select(r => r.WorkflowDefinitionId).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    private static (Guid Id, string Type) NewEntry() => (Guid.NewGuid(), $"wf-replay-{Guid.NewGuid():N}"[..20]);

    private static ContentCreated Created(Guid id, string type, string stage, ContentStatus status) =>
        new(id, type, new Dictionary<string, object> { ["Stage"] = stage }, status, Guid.NewGuid(), null,
            SensitivityLevel.Public, DateTime.UtcNow);

    private static ContentUpdated Updated(Guid id, string stage) =>
        new(id, new Dictionary<string, object> { ["Stage"] = stage }, Guid.NewGuid(), null, DateTime.UtcNow);

    private static ContentStatusChanged Status(Guid id, ContentStatus status) =>
        new(id, status, Guid.NewGuid(), DateTime.UtcNow);

    private async Task<IReadOnlyList<IEvent>> AppendAsync(string tenant, Guid id, string type, params object[] events)
    {
        await using var session = Store.LightweightSession(tenant);
        session.Events.StartStream<Content>(id, events);
        await session.SaveChangesAsync(Ct);

        var stored = await session.Events.FetchStreamAsync(id, token: Ct);
        stored.Should().HaveCount(events.Length);
        return stored;
    }

    private async Task<WorkflowDefinition> StoreWorkflowAsync(
        string tenant, string type, string triggerEvent, params (string Key, string Value)[] conditions)
    {
        var workflow = new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "replay probe",
            TriggerContentType = type,
            TriggerEvent = triggerEvent,
            Conditions = conditions.ToDictionary(c => c.Key, c => c.Value),
            Actions =
            [
                new WorkflowAction
                {
                    Type = "UpdateField",
                    Parameters = new Dictionary<string, string> { ["Field"] = "data.Stamp", ["Value"] = "fired" },
                },
            ],
        };

        await using var session = Store.LightweightSession(tenant);
        session.Store(workflow);
        await session.SaveChangesAsync(Ct);
        return workflow;
    }

    private async Task QueueRowAsync(string tenant, string eventType, Guid id, long sequence)
    {
        await using var session = Store.LightweightSession(tenant);
        session.Store(MissedWorkflowTrigger.For(eventType, id, tenant, sequence, new InvalidOperationException()));
        await session.SaveChangesAsync(Ct);
    }

    private async Task<int> SweepAsync(string tenant)
    {
        await using var session = Store.LightweightSession(tenant);
        return await MissedTriggerSweep.RunAsync(
            session, new WorkflowRunQueue(session, NullLogger<WorkflowRunQueue>.Instance), null, Ct);
    }

    private async Task<Content> LoadAsync(string tenant, Guid id)
    {
        await using var session = Store.QuerySession(tenant);
        return (await session.LoadAsync<Content>(id, Ct))!;
    }

    private async Task<IReadOnlyList<WorkflowRun>> RunsAsync(string tenant, Guid id)
    {
        await using var session = Store.QuerySession(tenant);
        return await session.Query<WorkflowRun>().Where(r => r.ContentId == id).ToListAsync(Ct);
    }

    /// <summary>Holds a session's first save until two sessions have asked to save. Later saves of the same session pass.</summary>
    private sealed class BarrierListener(Barrier barrier, Action reached) : DocumentSessionListenerBase
    {
        private bool _held;

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (_held) return;
            _held = true;
            reached();
            (await Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(30)), token))
                .Should().BeTrue("the other writer reached its save");
        }
    }
}
