using System.Diagnostics;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What the runner claims and in what order: a due run is not hidden by runs that are not due, a
/// tenant with a backlog does not keep the next one waiting, and a drain does not list partitions
/// once per action.
/// </summary>
/// <remarks>
/// Each test seeds a tenant of its own. The runner reads the twenty oldest candidates per tenant, so
/// in the default tenant the runs other classes left behind would decide what a test sees.
///
/// The fixture's hosted runner polls the same database and can claim anything seeded here. The
/// assertions are on what happened to a run, which holds whichever runner did it, and the actions
/// used need no handler on any particular host: "NoSuchAction" fails for good on its first attempt
/// everywhere, and <see cref="CountingRunnerAction"/> is registered on the fixture.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunnerFairClaimTests
{
    private const string NoHandler = "NoSuchAction";

    private readonly IntegrationTestFixture _factory;

    public WorkflowRunnerFairClaimTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    /// <summary>
    /// Exactly the candidate limit in backoff, all older than the one run that is due.
    /// </summary>
    /// <remarks>
    /// With fewer than twenty the due run is among the candidates either way and the test proves
    /// nothing. Filtering after the read, the twenty oldest are all skipped and the pass ends.
    /// </remarks>
    [Fact]
    public async Task A_due_run_behind_twenty_runs_in_backoff_is_executed()
    {
        var tenant = NewTenant();
        var old = DateTimeOffset.UtcNow.AddHours(-2);

        var waiting = new List<Guid>();
        for (var i = 0; i < WorkflowRunner.CandidatesPerPass; i++)
        {
            waiting.Add(await SeedAsync(tenant, NoHandler, old.AddSeconds(i), nextAttemptAt: ParkedUntil));
        }

        var due = await SeedAsync(tenant, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: null);

        await DrainAsync(NewRunner());

        var ran = await SettledAsync(tenant, due);
        ran.Actions[0].Status.Should().Be(AttemptStatus.Failed, "the due run was reached and its action ran");
        ran.Actions[0].Attempts.Should().Be(1);

        waiting.Should().HaveCount(20);
        foreach (var id in waiting)
        {
            var run = await LoadAsync(tenant, id);
            run.Actions[0].Attempts.Should().Be(0, "a run in backoff is left alone until its wait ends");
        }
    }

    /// <summary>
    /// Two runners draining the same due runs at once, with the hosted runner as a third.
    /// </summary>
    /// <remarks>
    /// Due-ness in the query means every node is offered the same runs at the same moment, so the
    /// optimistic claim is all that keeps an action from running twice. Counted in the action, since
    /// the run record reads one attempt either way.
    /// </remarks>
    [Fact]
    public async Task Two_runners_on_one_database_run_each_attempt_once()
    {
        var tenant = NewTenant();
        var contentId = Guid.NewGuid();

        await using (var session = Store.LightweightSession(tenant))
        {
            session.Store(new Content
            {
                Id = contentId,
                ContentType = "article",
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>(),
            });
            await session.SaveChangesAsync(Ct);
        }

        var runs = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            runs.Add(await SeedAsync(tenant, "CountingRunner", DateTimeOffset.UtcNow, nextAttemptAt: null, contentId));
        }

        await Task.WhenAll(
            Task.Run(() => DrainAsync(NewRunner()), Ct),
            Task.Run(() => DrainAsync(NewRunner()), Ct));

        runs.Should().HaveCount(12);
        foreach (var id in runs)
        {
            var run = await SettledAsync(tenant, id);
            run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);

            CountingRunnerAction.RunsByKey.GetValueOrDefault(run.Actions[0].IdempotencyKey)
                .Should().Be(1, "an attempt two nodes both ran is a message sent twice");
        }
    }

    /// <summary>
    /// A run stored before NextDueAt existed has no value, and is still claimed when it is due.
    /// </summary>
    [Fact]
    public async Task A_due_run_stored_without_a_next_due_value_is_still_executed()
    {
        var tenant = NewTenant();

        // Held back by a NextDueAt in the future while the field is removed, or a runner could
        // claim it first and the run under test would never have been without the field.
        var id = await SeedAsync(tenant, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: null, heldUntil: ParkedUntil);
        await RemoveNextDueAsync(id);

        await DrainAsync(NewRunner());

        var ran = await SettledAsync(tenant, id);
        ran.Actions[0].Status.Should().Be(AttemptStatus.Failed, "a missing value reads as due");
        ran.Actions[0].Attempts.Should().Be(1);
    }

    /// <summary>
    /// A run stored without the value that is not due gets the value written, so it stops taking one
    /// of the twenty candidate slots on every pass.
    /// </summary>
    [Fact]
    public async Task A_waiting_run_stored_without_a_next_due_value_has_it_written_on_first_look()
    {
        var tenant = NewTenant();
        var id = await SeedAsync(tenant, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: ParkedUntil);
        await RemoveNextDueAsync(id);

        await DrainAsync(NewRunner());

        var run = await LoadAsync(tenant, id);
        run.Actions[0].Attempts.Should().Be(0, "it is not due");
        run.NextDueAt.Should().Be(run.Actions[0].NextAttemptAt);
    }

    /// <summary>
    /// Two tenants, the one that sorts first holding more work. The second pass serves the other.
    /// </summary>
    [Fact]
    public async Task A_tenant_with_a_backlog_does_not_keep_the_next_tenant_waiting()
    {
        await DrainAsync(NewRunner());

        var prefix = NewTenant();
        var busy = prefix + "-a";
        var quiet = prefix + "-b";

        for (var i = 0; i < 4; i++)
        {
            await SeedAsync(busy, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: null);
        }

        var waiting = await SeedAsync(quiet, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: null);

        var runner = NewRunner();
        await runner.RunOnceAsync(Ct);
        await runner.RunOnceAsync(Ct);

        // Read at once rather than polled. The hosted runner reaches the quiet tenant within a few
        // seconds whatever the order, so waiting for the run to settle would pass without the
        // rotation.
        var run = await LoadAsync(quiet, waiting);
        run.Actions[0].Attempts.Should().Be(1, "the second pass starts after the tenant the first one served");
    }

    [Fact]
    public async Task An_idle_pass_lists_the_partitions_once()
    {
        var runner = NewRunner();

        int before;
        bool did;
        var passes = 0;

        do
        {
            before = runner.PartitionScans;
            did = await runner.RunOnceAsync(Ct);
            (++passes).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
        while (did);

        (runner.PartitionScans - before).Should().Be(1);
    }

    /// <summary>
    /// Draining a backlog used to list the partitions once per action claimed.
    /// </summary>
    [Fact]
    public async Task A_drain_does_not_list_the_partitions_once_per_action()
    {
        var tenant = NewTenant();
        for (var i = 0; i < 12; i++)
        {
            await SeedAsync(tenant, NoHandler, DateTimeOffset.UtcNow, nextAttemptAt: null);
        }

        var runner = NewRunner();
        var timer = Stopwatch.StartNew();
        var claims = 0;

        while (await runner.RunOnceAsync(Ct))
        {
            (++claims).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }

        timer.Stop();

        // The hosted runner can take some of the twelve. Losing ten of them to a runner that wakes
        // every five seconds has not been seen, and without this the bound below could hold for a
        // runner that claimed nothing.
        claims.Should().BeGreaterThan(2);

        // One list to start, one before reporting idle, and one more for each idle interval the
        // drain lasted, which is how long a kept list is trusted.
        var allowed = 2 + (int)(timer.Elapsed / WorkflowRunner.Idle);
        runner.PartitionScans.Should().BeLessThanOrEqualTo(allowed);
        runner.PartitionScans.Should().BeLessThan(claims);
    }

    /// <summary>
    /// The partition scan filters on the expression the Status index is built on.
    /// </summary>
    /// <remarks>
    /// Sequential scans are switched off for the plan so the answer is whether the index can serve
    /// the filter at all, not what the planner prefers on a table this small.
    /// </remarks>
    [Fact]
    public async Task The_partition_scan_can_use_the_status_index()
    {
        await using var conn = Store.Storage.Database.CreateConnection();
        await conn.OpenAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);

        await using (var off = conn.CreateCommand())
        {
            off.CommandText = "set local enable_seqscan = off";
            await off.ExecuteNonQueryAsync(Ct);
        }

        var plan = new List<string>();
        await using (var explain = conn.CreateCommand())
        {
            explain.CommandText = "explain " + WorkflowRunner.PartitionsWithWorkSql;
            await using var reader = await explain.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                plan.Add(reader.GetString(0));
            }
        }

        plan.Should().NotBeEmpty();
        string.Join('\n', plan).Should().Contain("mt_doc_workflow_runs_idx_status");
    }

    private static DateTimeOffset ParkedUntil => DateTimeOffset.UtcNow.AddHours(1);

    private static string NewTenant() => $"fair-{Guid.NewGuid():N}"[..21];

    private WorkflowRunner NewRunner() => new(
        _factory.Services,
        _factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
        _factory.Services.GetRequiredService<IConfiguration>());

    private static async Task DrainAsync(WorkflowRunner runner)
    {
        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
    }

    private async Task<Guid> SeedAsync(
        string tenant,
        string actionType,
        DateTimeOffset createdAt,
        DateTimeOffset? nextAttemptAt,
        Guid? contentId = null,
        DateTimeOffset? heldUntil = null)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Fair claim",
            ContentId = contentId ?? Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
            CreatedAt = createdAt,
        };

        run.Actions.Add(new WorkflowActionAttempt
        {
            Ordinal = 0,
            ActionType = actionType,
            IdempotencyKey = $"{run.Id:N}-0",
            NextAttemptAt = nextAttemptAt,
        });

        run.Recompute();
        if (heldUntil is not null) run.NextDueAt = heldUntil;

        await using var session = Store.LightweightSession(tenant);
        session.Store(run);
        await session.SaveChangesAsync(Ct);

        return run.Id;
    }

    /// <summary>Takes the stored run back to the shape it had before NextDueAt was kept.</summary>
    private async Task RemoveNextDueAsync(Guid runId)
    {
        await using var conn = Store.Storage.Database.CreateConnection();
        await conn.OpenAsync(Ct);

        await using (var strip = conn.CreateCommand())
        {
            strip.CommandText =
                $"update public.mt_doc_workflow_runs set data = data - 'NextDueAt' where id = '{runId}'";
            (await strip.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        await using var check = conn.CreateCommand();
        check.CommandText =
            $"select count(*) from public.mt_doc_workflow_runs where id = '{runId}' and jsonb_exists(data, 'NextDueAt')";
        Convert.ToInt64(await check.ExecuteScalarAsync(Ct)).Should().Be(0, "the stored run has to be without the field");
    }

    private async Task<WorkflowRun> LoadAsync(string tenant, Guid runId)
    {
        await using var session = Store.QuerySession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);

        run.Should().NotBeNull();
        run!.Actions.Should().HaveCount(1);
        return run;
    }

    /// <summary>The run once its action is neither waiting nor in flight, or as it stands after ten seconds.</summary>
    private async Task<WorkflowRun> SettledAsync(string tenant, Guid runId)
    {
        var run = await LoadAsync(tenant, runId);

        for (var i = 0; i < 100 && run.Actions[0].Status is AttemptStatus.Pending or AttemptStatus.Running; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Ct);
            run = await LoadAsync(tenant, runId);
        }

        return run;
    }
}
