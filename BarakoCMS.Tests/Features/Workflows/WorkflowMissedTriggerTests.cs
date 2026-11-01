using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A queue write that fails in the projection used to be logged and forgotten, so the event never
/// got a run. It is kept as a missed trigger now, and a sweep replays it.
/// </summary>
/// <remarks>
/// The projection is driven directly, as WorkflowTenantIsolationTests does, through a provider
/// whose queue throws on its first call. No event is stored: a Published trigger reads the entry
/// and nothing else, and storing one would let the host's own projection queue the run before the
/// test did.
/// </remarks>
[Collection("Integration1")]
public class WorkflowMissedTriggerTests
{
    private const long Sequence = 424242;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IntegrationTestFixture _fixture;

    public WorkflowMissedTriggerTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task An_enqueue_that_fails_once_leaves_a_missed_row_and_the_sweep_makes_exactly_one_run()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);

        await ProjectPublishedAsync(tenant, contentId, failQueueOnce: true, failSessions: false);

        await using var session = Store.LightweightSession(tenant);
        (await RunsAsync(session, contentId)).Should().BeEmpty("the enqueue failed");
        var missed = await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct);
        missed.Should().HaveCount(1);
        missed[0].EventType.Should().Be(WorkflowEvents.Published);
        missed[0].Sequence.Should().Be(Sequence);
        missed[0].TenantId.Should().Be(tenant);

        (await SweepAsync(session)).Should().Be(1);

        var runs = await RunsAsync(session, contentId);
        runs.Should().HaveCount(1);
        runs[0].TriggeringEventSequence.Should().Be(Sequence);
        (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct))
            .Should().BeEmpty("the replay removed the row");
    }

    [Fact]
    public async Task A_second_sweep_makes_no_run_even_when_the_row_comes_back()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);
        await ProjectPublishedAsync(tenant, contentId, failQueueOnce: true, failSessions: false);

        await using var session = Store.LightweightSession(tenant);
        (await SweepAsync(session)).Should().Be(1);
        (await RunsAsync(session, contentId)).Should().HaveCount(1);

        (await SweepAsync(session)).Should().Be(0, "nothing is left to replay");

        // A crash between the run's save and the row's delete leaves the row behind.
        session.Store(MissedWorkflowTrigger.For(
            WorkflowEvents.Published, contentId, tenant, Sequence, new InvalidOperationException()));
        await session.SaveChangesAsync(Ct);

        (await SweepAsync(session)).Should().Be(1, "the row was replayed and removed");
        (await RunsAsync(session, contentId)).Should().HaveCount(1, "a run at that sequence exists, so none is added");
    }

    [Fact]
    public async Task A_failed_write_of_the_missed_row_does_not_escape_the_projection()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);

        var act = () => ProjectPublishedAsync(tenant, contentId, failQueueOnce: true, failSessions: true);

        await act.Should().NotThrowAsync("an exception that escapes stops the projection shard");
        await using var session = Store.QuerySession(tenant);
        (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_whose_sweep_fails_does_not_stop_the_replay_for_the_tenants_after_it()
    {
        // The failing tenant is stored first, so it is listed first, which is when an escaping
        // exception would cost the tenants after it their turn.
        var failing = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var healthy = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var alsoHealthy = await TenantProfileSeed.TenantAsync(_fixture, Ct);

        var contents = new Dictionary<string, Guid>();
        foreach (var tenant in new[] { failing, healthy, alsoHealthy })
        {
            var contentId = await SeedAsync(tenant);
            contents[tenant] = contentId;
            await using var session = Store.LightweightSession(tenant);
            session.Store(MissedWorkflowTrigger.For(
                WorkflowEvents.Published, contentId, tenant, Sequence, new InvalidOperationException()));
            await session.SaveChangesAsync(Ct);
        }

        var logger = _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>();
        var service = new ScheduledContentService(Store, logger)
        {
            BeforeMissedTriggers = (session, _) => session.TenantId == failing
                ? throw new InvalidOperationException("a database error for one tenant")
                : Task.CompletedTask,
        };

        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, Ct)).Should().BeTrue("nothing else holds the sweep lock");

        foreach (var tenant in new[] { healthy, alsoHealthy })
        {
            await using var session = Store.QuerySession(tenant);
            (await RunsAsync(session, contents[tenant])).Should().HaveCount(1, $"tenant {tenant} was replayed");
            (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contents[tenant]).ToListAsync(Ct))
                .Should().BeEmpty();
        }

        await using var left = Store.QuerySession(failing);
        (await RunsAsync(left, contents[failing])).Should().BeEmpty("its replay never started");
        (await left.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contents[failing]).ToListAsync(Ct))
            .Should().HaveCount(1, "its row stays for the next sweep");
    }

    private async Task<Guid> SeedAsync(string tenant)
    {
        var contentType = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = Guid.NewGuid();

        await using var session = Store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = contentId,
            ContentType = contentType,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>(),
        });
        session.Store(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "missed trigger probe",
            TriggerContentType = contentType,
            TriggerEvent = WorkflowEvents.Published,
            Actions =
            [
                new WorkflowAction
                {
                    Type = "UpdateField",
                    Parameters = new Dictionary<string, string> { ["Field"] = "data.Stamp", ["Value"] = "fired" },
                },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return contentId;
    }

    private static Task<int> SweepAsync(IDocumentSession session) =>
        MissedTriggerSweep.RunAsync(session, new WorkflowRunQueue(session, NullLogger<WorkflowRunQueue>.Instance), null, Ct);

    private static async Task<IReadOnlyList<WorkflowRun>> RunsAsync(IQuerySession session, Guid contentId) =>
        await session.Query<WorkflowRun>().Where(r => r.ContentId == contentId).ToListAsync(Ct);

    private async Task ProjectPublishedAsync(string tenant, Guid contentId, bool failQueueOnce, bool failSessions)
    {
        var services = new FlakyServices(_fixture.Services, failQueueOnce, failSessions);

        var envelope = Substitute.For<IEvent<barakoCMS.Events.ContentStatusChanged>>();
        envelope.Data.Returns(new barakoCMS.Events.ContentStatusChanged(contentId, ContentStatus.Published, Guid.NewGuid()));
        envelope.TenantId.Returns(tenant);
        envelope.Sequence.Returns(Sequence);

        await using var ops = Store.LightweightSession(tenant);
        await new WorkflowProjection(services).Project(envelope, ops, CancellationToken.None);
    }

    /// <summary>The host's services, with a queue that throws on its first call and sessions that can be made to throw.</summary>
    private sealed class FlakyServices(IServiceProvider inner, bool failQueueOnce, bool failSessions)
        : IServiceProvider, IServiceScopeFactory
    {
        private int _queueCalls;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new FlakyScope(inner.CreateScope(), this);

        internal IWorkflowRunQueue Queue(IWorkflowRunQueue real)
        {
            var queue = Substitute.For<IWorkflowRunQueue>();
            queue.EnqueueAsync(default!, default!, default, default)
                .ReturnsForAnyArgs(call => failQueueOnce && Interlocked.Increment(ref _queueCalls) == 1
                    ? throw new InvalidOperationException("a database error while queueing")
                    : real.EnqueueAsync(call.ArgAt<Content>(0), call.ArgAt<string>(1), call.ArgAt<long>(2), call.ArgAt<CancellationToken>(3)));
            return queue;
        }

        internal bool FailSessions => failSessions;
    }

    private sealed class FlakyScope(IServiceScope real, FlakyServices owner) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IWorkflowRunQueue))
            {
                return owner.Queue((IWorkflowRunQueue)real.ServiceProvider.GetRequiredService(serviceType));
            }

            if (serviceType == typeof(IDocumentSession) && owner.FailSessions)
            {
                throw new InvalidOperationException("the database is not reachable");
            }

            return real.ServiceProvider.GetService(serviceType);
        }

        public void Dispose() => real.Dispose();
    }
}
