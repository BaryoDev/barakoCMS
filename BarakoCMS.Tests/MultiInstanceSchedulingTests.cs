using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Running more than one instance must not make anything happen twice.
/// </summary>
/// <remarks>
/// Both mechanisms that act on their own schedule had this problem and each needed a different
/// answer. The projection daemon ran <c>Solo</c>, which Marten documents as assuming a single node,
/// so every node processed every event. The scheduled-content sweep is a BackgroundService with no
/// leader election at all.
///
/// The symptom in both cases is a workflow action running once per node. For an email or a webhook
/// action that reaches the outside world, so it is not something the next deploy quietly fixes.
/// </remarks>
[Collection("Integration2")]
public class MultiInstanceSchedulingTests
{
    private readonly IntegrationTestFixture _fixture;

    public MultiInstanceSchedulingTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A sweep declines while another holds the lock.
    /// </summary>
    /// <remarks>
    /// The lock is taken here rather than by racing two sweeps against each other. Two concurrent
    /// calls only contend if they happen to overlap, and a sweep with nothing to do finishes in
    /// milliseconds, so they can serialise and both legitimately succeed. That test would pass or
    /// fail on timing, which for a lock is the one thing it must not do.
    ///
    /// Holding the lock explicitly means the assertion is about the lock and nothing else.
    /// </remarks>
    [Fact]
    public async Task A_sweep_declines_while_another_instance_holds_the_lock()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        await using var holder = store.Storage.Database.CreateConnection();
        await holder.OpenAsync();

        await using (var take = holder.CreateCommand())
        {
            take.CommandText = "select pg_advisory_lock(8242026001)";
            await take.ExecuteScalarAsync();
        }

        try
        {
            var swept = await new ScheduledContentService(store, logger)
                .TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None);

            swept.Should().BeFalse(
                "another connection holds the sweep lock, so this instance must decline rather than "
                + "transition the same content a second time");
        }
        finally
        {
            await using var release = holder.CreateCommand();
            release.CommandText = "select pg_advisory_unlock(8242026001)";
            await release.ExecuteScalarAsync();
        }
    }

    /// <summary>The lock is available again once the holder lets go.</summary>
    [Fact]
    public async Task A_sweep_proceeds_once_the_lock_is_free()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var swept = await new ScheduledContentService(store, logger)
            .TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None);

        swept.Should().BeTrue("nothing holds the lock, so the sweep must run");
    }

    /// <summary>
    /// The lock is released, so the next tick is not blocked by the previous one.
    /// </summary>
    /// <remarks>
    /// A lock that is taken and never released turns a duplicate-work bug into a no-work bug, which
    /// is quieter and worse: scheduled content simply stops publishing and nothing errors.
    /// </remarks>
    [Fact]
    public async Task The_lock_is_released_after_a_sweep()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var logger = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

        var service = new ScheduledContentService(store, logger);

        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
        (await service.TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None)).Should().BeTrue(
            "a second sweep after the first has finished must be able to take the lock again");
    }

    /// <summary>
    /// Scheduled content transitions exactly once even when two instances sweep together.
    /// </summary>
    /// <remarks>
    /// The end-to-end version of the first test. Counts events on the stream rather than the
    /// document's final state, because the document looks identical whether it was transitioned once
    /// or twice. The stream is where the duplication is visible, and the stream is what the workflow
    /// projection reads.
    ///
    /// The count is polled rather than read once. ScheduledContentService is registered as a hosted
    /// service, so a third sweeper is running on a timer inside the test host and takes the same
    /// advisory lock. If it holds that lock while the two sweeps below run, both decline and the
    /// stream has no transition yet, which failed this test in CI with a count of 0 while it passed
    /// every time in isolation.
    ///
    /// Polling does not weaken it. The invariant is "exactly one transition, however many sweepers
    /// there are", so waiting for the first and then asserting there is only one is the invariant
    /// stated properly. A stream that never transitions still fails, on the timeout, with the same
    /// message.
    /// </remarks>
    [Fact]
    public async Task Due_content_transitions_once_when_two_instances_sweep_together()
    {
        var id = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();

            var content = await writer.CreateAsync(new ContentCreated(
                id, "scheduled-article", new Dictionary<string, object> { ["Title"] = "due" },
                ContentStatus.Draft, author, "due", SensitivityLevel.Public), default);

            await writer.AppendAsync(content, new ContentScheduled(
                id, DateTime.UtcNow.AddMinutes(-5), null, author), default);

            await session.SaveChangesAsync();
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var logger = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

            await Task.WhenAll(
                new ScheduledContentService(store, logger).SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None),
                new ScheduledContentService(store, logger).SweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None));
        }

        var transitions = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var scope = _fixture.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stream = await session.Events.FetchStreamAsync(id);
            transitions = stream.Count(e => e.Data is ContentStatusChanged);
            if (transitions > 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        transitions.Should().Be(1,
            "two ContentStatusChanged events means the workflow projection fires every Published "
            + "workflow twice, and an email action sends two emails");
    }

    /// <summary>
    /// A leader that loses its lock mid-sweep and a second instance that takes over together make one
    /// transition per item, one Published workflow run per item.
    /// </summary>
    /// <remarks>
    /// The holder's backend is terminated after it has loaded its batch, which is what a pooler
    /// dropping the lock connection or a failover looks like from the sweep. The second instance
    /// then sweeps the same items to completion, and the first carries on with its stale batch.
    /// Without the re-check inside the write the first appends a second transition for every item.
    /// Each item gets a workflow run through the CreateTask action; a webhook action would count the
    /// same runs, one per Published event.
    /// </remarks>
    [Fact]
    public async Task A_leader_that_loses_its_lock_mid_sweep_and_a_second_instance_transition_each_item_once()
    {
        var contentType = "sched-idem-" + Guid.NewGuid().ToString("N")[..8];
        var author = Guid.NewGuid();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var workflowId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();

            session.Store(new WorkflowDefinition
            {
                Id = workflowId,
                Name = "idempotent-" + Guid.NewGuid().ToString("N"),
                TriggerContentType = contentType,
                TriggerEvent = "Published",
                Actions =
                [
                    new WorkflowAction
                    {
                        Type = "CreateTask",
                        Parameters = new Dictionary<string, string>
                        {
                            ["ContentType"] = contentType + "-probe",
                            ["Title"] = "probe",
                        },
                    },
                ],
            });

            foreach (var id in ids)
            {
                var content = await writer.CreateAsync(new ContentCreated(
                    id, contentType, new Dictionary<string, object> { ["Title"] = "due" },
                    ContentStatus.Draft, author, "due", SensitivityLevel.Public), default);

                await writer.AppendAsync(content, new ContentScheduled(
                    id, DateTime.UtcNow.AddMinutes(-5), null, author), default);
            }

            await session.SaveChangesAsync();
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            var logger = scope.ServiceProvider
                .GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScheduledContentService>>();

            var killed = 0;
            var firstLeader = new ScheduledContentService(store, logger)
            {
                AfterLoad = async ct =>
                {
                    await using var admin = store.Storage.Database.CreateConnection();
                    await admin.OpenAsync(ct);
                    await using var kill = admin.CreateCommand();
                    kill.CommandText =
                        "select count(*) from (select pg_terminate_backend(pid) from pg_locks "
                        + "where locktype = 'advisory' and granted "
                        + "and ((classid::bigint << 32) | objid::bigint) = 8242026001) t";
                    killed = Convert.ToInt32(await kill.ExecuteScalarAsync(ct));

                    var second = new ScheduledContentService(store, logger);
                    var swept = false;
                    for (var attempt = 0; attempt < 60 && !swept; attempt++)
                    {
                        swept = await second.TrySweepAllTenantsAsync(DateTime.UtcNow, ct);
                        if (!swept) await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                    }

                    swept.Should().BeTrue("the lock was freed when its holder was terminated");
                },
            };

            // True or false: it lost the lock, so it may stop before a later tenant and say so.
            await firstLeader.TrySweepAllTenantsAsync(DateTime.UtcNow, CancellationToken.None);

            killed.Should().Be(1, "exactly one backend held the sweep lock and it was terminated");
        }

        var runs = new List<WorkflowRun>();
        for (var attempt = 0; attempt < 40 && runs.Count < ids.Count; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            using var scope = _fixture.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            runs = (await query.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync()).ToList();
        }

        // Long enough for a duplicate event to reach the projection if one was written.
        await Task.Delay(TimeSpan.FromSeconds(3));
        using (var scope = _fixture.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            runs = (await query.Query<WorkflowRun>().Where(r => r.WorkflowDefinitionId == workflowId).ToListAsync()).ToList();

            runs.Should().HaveCount(ids.Count, "one Published run per item, whatever the number of leaders");
            foreach (var id in ids)
            {
                runs.Count(r => r.ContentId == id).Should().Be(1);

                var stream = await query.Events.FetchStreamAsync(id);
                stream.Should().NotBeEmpty();
                stream.Count(e => e.Data is ContentStatusChanged).Should().Be(1,
                    "a second ContentStatusChanged means the stale leader repeated the transition");
            }
        }
    }
}
