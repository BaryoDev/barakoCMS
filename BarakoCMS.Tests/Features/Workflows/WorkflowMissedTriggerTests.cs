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
/// whose queue throws on its first call. The events here are not stored: a Published trigger reads
/// the entry and nothing else, and storing one would let the host's own projection queue the run
/// before the test did. WorkflowMissedTriggerReplayTests stores real events.
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

        await ProjectPublishedAsync(tenant, contentId, opsTenant: tenant);

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
        await ProjectPublishedAsync(tenant, contentId, opsTenant: tenant);

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
    public async Task A_missed_row_staged_on_operations_opened_for_another_tenant_lands_in_the_events_tenant()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);

        // The projection reads the entry through the operations, which here are the default
        // partition's, so the entry has to be readable there or it never reaches the queue and
        // nothing fails. The queue's own scope is the event's tenant, where the workflow is.
        await using (var seed = Store.LightweightSession())
        {
            seed.Store(new Content
            {
                Id = contentId,
                ContentType = "default-partition-copy",
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>(),
            });
            await seed.SaveChangesAsync(Ct);
        }

        await ProjectPublishedAsync(tenant, contentId, opsTenant: null);

        await using var named = Store.QuerySession(tenant);
        (await named.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct))
            .Should().HaveCount(1, "the row belongs to the tenant of the event");

        await using var other = Store.QuerySession();
        (await other.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct))
            .Should().BeEmpty("the operations were opened for the default partition");
    }

    [Fact]
    public async Task A_row_that_fails_every_replay_is_left_alone_after_the_last_attempt()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);
        await using var session = Store.LightweightSession(tenant);
        session.Store(MissedWorkflowTrigger.For(
            WorkflowEvents.Published, contentId, tenant, Sequence, new InvalidOperationException()));
        await session.SaveChangesAsync(Ct);

        var queue = Substitute.For<IWorkflowRunQueue>();
        queue.EnqueueAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<Task<int>>(_ => throw new InvalidOperationException("a database error while queueing"));
        var logger = new RecordingLogger();

        for (var attempt = 1; attempt <= MissedTriggerSweep.MaxAttempts; attempt++)
        {
            (await MissedTriggerSweep.RunAsync(session, queue, logger, Ct)).Should().Be(0);
            var row = (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct)).Single();
            row.Attempts.Should().Be(attempt);
            row.LastError.Should().Be(nameof(InvalidOperationException), "the type name only");
        }

        await queue.Received(MissedTriggerSweep.MaxAttempts)
            .EnqueueAsync(Arg.Any<Content>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(MissedTriggerSweep.MaxAttempts - 1);
        var stopped = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        stopped.Should().HaveCount(1, "the last attempt says the row is stopped");
        stopped[0].Message.Should().Contain("stopped").And.Contain("Attempts back to 0");
        logger.Entries.Should().OnlyContain(e => !e.Message.Contains("a database error"), "the exception message is not logged");

        (await MissedTriggerSweep.RunAsync(session, queue, logger, Ct)).Should().Be(0);
        await queue.Received(MissedTriggerSweep.MaxAttempts)
            .EnqueueAsync(Arg.Any<Content>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_row_with_fewer_attempts_is_read_before_one_that_has_failed_more()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var tried = await SeedAsync(tenant);
        var fresh = await SeedAsync(tenant);
        await using var session = Store.LightweightSession(tenant);

        var old = MissedWorkflowTrigger.For(WorkflowEvents.Published, tried, tenant, Sequence, new InvalidOperationException());
        old.RecordedAt = DateTime.UtcNow.AddHours(-2);
        old.Attempts = 5;
        var recent = MissedWorkflowTrigger.For(WorkflowEvents.Published, fresh, tenant, Sequence + 1, new InvalidOperationException());
        recent.RecordedAt = DateTime.UtcNow;
        session.Store(old, recent);
        await session.SaveChangesAsync(Ct);

        var order = new List<Guid>();
        var queue = Substitute.For<IWorkflowRunQueue>();
        queue.EnqueueAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            order.Add(call.ArgAt<Content>(0).Id);
            return Task.FromResult(1);
        });

        (await MissedTriggerSweep.RunAsync(session, queue, null, Ct)).Should().Be(2);
        order.Should().Equal(fresh, tried);
    }

    [Fact]
    public async Task A_missed_row_of_a_deleted_entry_is_removed_without_a_run()
    {
        var tenant = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = await SeedAsync(tenant);
        await using var session = Store.LightweightSession(tenant);
        session.Delete<Content>(contentId);
        session.Store(MissedWorkflowTrigger.For(
            WorkflowEvents.Published, contentId, tenant, Sequence, new InvalidOperationException()));
        await session.SaveChangesAsync(Ct);

        (await SweepAsync(session)).Should().Be(1);

        (await RunsAsync(session, contentId)).Should().BeEmpty();
        (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_tenant_whose_replay_fails_does_not_stop_the_replay_for_the_other_tenants()
    {
        var failing = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var healthy = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var alsoHealthy = await TenantProfileSeed.TenantAsync(_fixture, Ct);

        var contents = await SeedRowsAsync(failing, healthy, alsoHealthy);

        var visited = new List<string>();
        var service = ServiceWith(hook: (session, _) =>
        {
            visited.Add(session.TenantId);
            return session.TenantId == failing
                ? throw new InvalidOperationException("a database error for one tenant")
                : Task.CompletedTask;
        });

        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, Ct)).Should().BeTrue("nothing else holds the sweep lock");

        visited.Should().Contain([failing, healthy, alsoHealthy], "every partition is visited whatever order the tenants are listed in");
        foreach (var tenant in new[] { healthy, alsoHealthy })
        {
            (await RunCountAsync(tenant, contents[tenant])).Should().Be(1, $"tenant {tenant} was replayed");
            (await RowCountAsync(tenant, contents[tenant])).Should().Be(0);
        }

        (await RunCountAsync(failing, contents[failing])).Should().Be(0, "its replay never started");
        (await RowCountAsync(failing, contents[failing])).Should().Be(1, "its row stays for the next sweep");
    }

    [Fact]
    public async Task The_replay_still_runs_when_the_sweep_of_due_content_throws()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var contents = await SeedRowsAsync(tenant);

        await using (var session = Store.LightweightSession(tenant))
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = $"wf-missed-{Guid.NewGuid():N}"[..20],
                Status = ContentStatus.Scheduled,
                ScheduledPublishAt = DateTime.UtcNow.AddHours(-1),
                Data = new Dictionary<string, object>(),
            });
            await session.SaveChangesAsync(Ct);
        }

        var service = ServiceWith(hook: null);
        service.AfterLoad = _ => throw new InvalidOperationException("a database error in the content sweep");

        try
        {
            await service.TrySweepAllTenantsAsync(DateTime.UtcNow, Ct);
        }
        catch (InvalidOperationException ex)
        {
            ex.Message.Should().Contain("content sweep", "a failure that surfaces is the sweep's own, not one from the replay");
        }

        (await RunCountAsync(tenant, contents[tenant])).Should().Be(1, "the replay does not depend on the other sweeps");
        (await RowCountAsync(tenant, contents[tenant])).Should().Be(0);
    }

    [Fact]
    public async Task A_row_of_a_tenant_that_is_not_active_is_still_replayed()
    {
        var tenant = await TenantProfileSeed.TenantAsync(_fixture, Ct, t => t.Status = TenantStatus.Suspended);
        var contents = await SeedRowsAsync(tenant);

        (await ServiceWith(hook: null).TrySweepAllTenantsAsync(DateTime.UtcNow, Ct)).Should().BeTrue();

        (await RunCountAsync(tenant, contents[tenant])).Should().Be(1, "the event happened before the tenant was deactivated");
        (await RowCountAsync(tenant, contents[tenant])).Should().Be(0);
    }

    [Fact]
    public async Task A_row_of_the_default_partition_is_replayed()
    {
        var contentId = await SeedAsync(tenant: null);
        await using (var session = Store.LightweightSession())
        {
            session.Store(MissedWorkflowTrigger.For(
                WorkflowEvents.Published, contentId, session.TenantId, Sequence, new InvalidOperationException()));
            await session.SaveChangesAsync(Ct);
        }

        (await ServiceWith(hook: null).TrySweepAllTenantsAsync(DateTime.UtcNow, Ct)).Should().BeTrue();

        await using var check = Store.QuerySession();
        (await RunsAsync(check, contentId)).Should().HaveCount(1);
        (await check.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct)).Should().BeEmpty();
    }

    private ScheduledContentService ServiceWith(Func<IDocumentSession, CancellationToken, Task>? hook) =>
        new(Store, _fixture.Services.GetRequiredService<ILogger<ScheduledContentService>>())
        {
            BeforeMissedTriggers = hook,
        };

    private async Task<Dictionary<string, Guid>> SeedRowsAsync(params string[] tenants)
    {
        var contents = new Dictionary<string, Guid>();
        foreach (var tenant in tenants)
        {
            var contentId = await SeedAsync(tenant);
            contents[tenant] = contentId;
            await using var session = Store.LightweightSession(tenant);
            session.Store(MissedWorkflowTrigger.For(
                WorkflowEvents.Published, contentId, tenant, Sequence, new InvalidOperationException()));
            await session.SaveChangesAsync(Ct);
        }

        return contents;
    }

    private async Task<int> RunCountAsync(string tenant, Guid contentId)
    {
        await using var session = Store.QuerySession(tenant);
        return (await RunsAsync(session, contentId)).Count;
    }

    private async Task<int> RowCountAsync(string tenant, Guid contentId)
    {
        await using var session = Store.QuerySession(tenant);
        return (await session.Query<MissedWorkflowTrigger>().Where(r => r.ContentId == contentId).ToListAsync(Ct)).Count;
    }

    private async Task<Guid> SeedAsync(string? tenant)
    {
        var contentType = $"wf-missed-{Guid.NewGuid():N}"[..20];
        var contentId = Guid.NewGuid();

        await using var session = tenant is null ? Store.LightweightSession() : Store.LightweightSession(tenant);
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

    /// <param name="opsTenant">The tenant the daemon's operations are opened for, or null for the default partition.</param>
    private async Task ProjectPublishedAsync(string tenant, Guid contentId, string? opsTenant)
    {
        var services = new FlakyServices(_fixture.Services);

        var envelope = Substitute.For<IEvent<barakoCMS.Events.ContentStatusChanged>>();
        envelope.Data.Returns(new barakoCMS.Events.ContentStatusChanged(
            contentId, ContentStatus.Published, Guid.NewGuid(), DateTime.UtcNow));
        envelope.TenantId.Returns(tenant);
        envelope.Sequence.Returns(Sequence);

        await using var ops = opsTenant is null ? Store.LightweightSession() : Store.LightweightSession(opsTenant);
        await new WorkflowProjection(services).Project(envelope, ops, CancellationToken.None);
        await ops.SaveChangesAsync(Ct);
    }

    /// <summary>The host's services, with a queue that throws on its first call.</summary>
    private sealed class FlakyServices(IServiceProvider inner) : IServiceProvider, IServiceScopeFactory
    {
        private int _queueCalls;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceScopeFactory) ? this : inner.GetService(serviceType);

        public IServiceScope CreateScope() => new FlakyScope(inner.CreateScope(), this);

        internal IWorkflowRunQueue Queue(IWorkflowRunQueue real)
        {
            var queue = Substitute.For<IWorkflowRunQueue>();
            queue.EnqueueAsync(default!, default!, default, default)
                .ReturnsForAnyArgs(call => Interlocked.Increment(ref _queueCalls) == 1
                    ? throw new InvalidOperationException("a database error while queueing")
                    : real.EnqueueAsync(call.ArgAt<Content>(0), call.ArgAt<string>(1), call.ArgAt<long>(2), call.ArgAt<CancellationToken>(3)));
            return queue;
        }
    }

    private sealed class FlakyScope(IServiceScope real, FlakyServices owner) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IWorkflowRunQueue)
                ? owner.Queue((IWorkflowRunQueue)real.ServiceProvider.GetRequiredService(serviceType))
                : real.ServiceProvider.GetService(serviceType);

        public void Dispose() => real.Dispose();
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " " + exception)));
    }
}
