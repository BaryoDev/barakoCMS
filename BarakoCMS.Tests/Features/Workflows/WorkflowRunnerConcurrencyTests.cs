using System.Diagnostics;
using System.Globalization;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The runner's bound on actions in flight: runs of different queues overlap up to it and never
/// past it, the actions of one run stay in order, tenants share the slots, and one failing action
/// leaves the others alone.
/// </summary>
/// <remarks>
/// Every test that touches the database stops the fixture's hosted runner and drains what other
/// classes left due, so the runner the test builds is the only one and its bound is the one in
/// force. Each seeds tenants of its own. The times asserted on are derived from the action's delay
/// and the number of runs.
///
/// A retry another class left behind can still come due between the drain and the pass and take a
/// slot, and the runner has no way to be shown only some tenants. So the tests give the pass more
/// slots than they need where they can, drain where one pass is not the point, and assert on their
/// own runs.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunnerConcurrencyTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunnerConcurrencyTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public void The_bound_is_one_when_nothing_is_configured()
    {
        WorkflowRunner.ReadConcurrency(Config(null)).Should().Be(1);
        WorkflowRunner.DefaultConcurrency.Should().Be(1);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("20", 20)]
    public void A_bound_in_range_is_read(string configured, int expected)
    {
        WorkflowRunner.ReadConcurrency(Config(configured)).Should().Be(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("21")]
    [InlineData("100000")]
    public void A_bound_out_of_range_is_refused_by_name(string configured)
    {
        FluentActions.Invoking(() => WorkflowRunner.ReadConcurrency(Config(configured)))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"*{WorkflowRunner.ConcurrencyKey}*");
    }

    /// <summary>
    /// The host builds the runner when it starts, so a refused value stops the start.
    /// </summary>
    [Fact]
    public void A_runner_is_not_built_with_a_bound_out_of_range()
    {
        FluentActions.Invoking(() => NewRunner(0))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"*{WorkflowRunner.ConcurrencyKey}*");
    }

    /// <summary>
    /// Eight runs of one slow action with a bound of four take about two delays, not eight.
    /// </summary>
    /// <remarks>
    /// The delay is a floor on each action, so one at a time cannot finish under eight delays. The
    /// limit asserted is one delay under that, which leaves five delays of room over the best case
    /// of two for a loaded machine or a slow database. The count of actions in flight is what shows
    /// the overlap; the time shows that the overlap bought something.
    /// </remarks>
    [Fact]
    public async Task Runs_of_a_slow_action_finish_well_under_the_serial_time_with_a_bound_of_four()
    {
        const int runs = 8, bound = 4, delayMs = 750;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var seeded = new List<Guid>();
            for (var i = 0; i < runs; i++)
            {
                seeded.Add(await SeedAsync(tenant, contentId, Slow(group, delayMs)));
            }

            var timer = Stopwatch.StartNew();
            await DrainAsync(NewRunner(bound));
            timer.Stop();

            var gauge = SlowRunnerAction.Groups[group];

            seeded.Should().HaveCount(runs);
            foreach (var id in seeded)
            {
                var run = await LoadAsync(tenant, id, actions: 1);
                run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
                gauge.TimesRun(run.Actions[0].IdempotencyKey).Should().Be(1, "a run executed twice is a message sent twice");
            }

            var serial = TimeSpan.FromMilliseconds(delayMs * runs);
            var best = TimeSpan.FromMilliseconds(delayMs * (runs / bound));
            var limit = serial - TimeSpan.FromMilliseconds(delayMs);

            timer.Elapsed.Should().BeLessThan(limit,
                $"one at a time takes at least {serial.TotalSeconds:0.0}s and four at a time about {best.TotalSeconds:0.0}s");
            gauge.MostAtOnce.Should().BeInRange(2, bound);
        });
    }

    /// <summary>
    /// The same shape with the bound at one is serial: never two in flight, and no faster than the
    /// delays added up.
    /// </summary>
    [Fact]
    public async Task With_a_bound_of_one_the_runs_execute_one_at_a_time()
    {
        const int runs = 4, delayMs = 750;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var seeded = new List<Guid>();
            for (var i = 0; i < runs; i++)
            {
                seeded.Add(await SeedAsync(tenant, contentId, Slow(group, delayMs)));
            }

            var timer = Stopwatch.StartNew();
            await DrainAsync(NewRunner(1));
            timer.Stop();

            seeded.Should().HaveCount(runs);
            foreach (var id in seeded)
            {
                (await LoadAsync(tenant, id, actions: 1)).Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
            }

            SlowRunnerAction.Groups[group].MostAtOnce.Should().Be(1);

            // A tenth off, because a timer may fire a little early.
            timer.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(delayMs * runs * 0.9));
        });
    }

    /// <summary>
    /// Six tenants with two runs each and a bound of three: never more than three in flight.
    /// </summary>
    /// <remarks>
    /// More tenants than slots on purpose. A round gives every tenant with work a slot, so a round
    /// that does not stop when the slots are full starts six.
    /// </remarks>
    [Fact]
    public async Task No_more_than_the_bound_are_in_flight_across_tenants()
    {
        const int tenants = 6, runsEach = 2, bound = 3, delayMs = 500;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var prefix = NewTenant();
            var group = NewGroup();
            var seeded = new List<(string Tenant, Guid Run)>();

            for (var t = 0; t < tenants; t++)
            {
                var tenant = $"{prefix}-{(char)('a' + t)}";
                var contentId = await StoreContentAsync(tenant);

                for (var i = 0; i < runsEach; i++)
                {
                    seeded.Add((tenant, await SeedAsync(tenant, contentId, Slow(group, delayMs))));
                }
            }

            await DrainAsync(NewRunner(bound));

            var gauge = SlowRunnerAction.Groups[group];

            seeded.Should().HaveCount(tenants * runsEach);
            foreach (var (tenant, id) in seeded)
            {
                var run = await LoadAsync(tenant, id, actions: 1);
                run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
                gauge.TimesRun(run.Actions[0].IdempotencyKey).Should().Be(1);
            }

            gauge.MostAtOnce.Should().BeLessThanOrEqualTo(bound, "the bound is per node, whatever the number of tenants");
            gauge.MostAtOnce.Should().BeGreaterThan(1, "runs of different tenants overlap");
        });
    }

    /// <summary>
    /// One tenant with eight due runs, one with two, and four slots: the first pass splits its slots
    /// between them.
    /// </summary>
    /// <remarks>
    /// Filling the slots from the first tenant that has work gives it all four, and the tenant with
    /// two waits a pass for every four runs the other has queued.
    ///
    /// Asserted as a difference of at most one and not as two and two, so a stray run from another
    /// class taking a slot does not fail it. At least two of the test's own have to have run, or
    /// the difference says nothing.
    /// </remarks>
    [Fact]
    public async Task A_tenant_with_a_long_queue_does_not_take_every_slot_of_a_pass()
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var prefix = NewTenant();
            var group = NewGroup();
            var queues = new Dictionary<string, int> { [prefix + "-a"] = 8, [prefix + "-b"] = 2 };
            var seeded = new Dictionary<string, List<Guid>>();

            foreach (var (tenant, count) in queues)
            {
                var contentId = await StoreContentAsync(tenant);
                seeded[tenant] = [];

                for (var i = 0; i < count; i++)
                {
                    seeded[tenant].Add(await SeedAsync(tenant, contentId, Slow(group, 100)));
                }
            }

            (await NewRunner(4).RunOnceAsync(Ct)).Should().BeTrue("ten runs are due");

            var executed = new Dictionary<string, int>();
            foreach (var (tenant, ids) in seeded)
            {
                executed[tenant] = 0;
                foreach (var id in ids)
                {
                    if ((await LoadAsync(tenant, id, actions: 1)).Actions[0].Attempts == 1) executed[tenant]++;
                }
            }

            executed.Should().HaveCount(2);
            executed.Values.Sum().Should().BeInRange(2, 4, "the pass has four slots and ten of these runs are due");
            Math.Abs(executed[prefix + "-a"] - executed[prefix + "-b"]).Should().BeLessThanOrEqualTo(1,
                "the slots of a pass are handed out a round of the tenants at a time");

            await DrainAsync(NewRunner(4));
        });
    }

    /// <summary>
    /// Two runs of three actions each, with free slots throughout: the runs overlap, and inside each
    /// run an action starts only after the one before it ended.
    /// </summary>
    /// <remarks>
    /// The first action is the slowest, so a runner that started the second while the first was out
    /// would show it here.
    /// </remarks>
    [Fact]
    public async Task The_actions_of_one_run_stay_in_order_with_a_bound_above_one()
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var seeded = new List<Guid>
            {
                await SeedAsync(tenant, contentId, Slow(group, 400), Slow(group, 100), Slow(group, 0)),
                await SeedAsync(tenant, contentId, Slow(group, 400), Slow(group, 100), Slow(group, 0)),
            };

            await DrainAsync(NewRunner(4));

            var gauge = SlowRunnerAction.Groups[group];
            var events = gauge.Events;

            seeded.Should().HaveCount(2);
            foreach (var id in seeded)
            {
                var run = await LoadAsync(tenant, id, actions: 3);
                run.Status.Should().Be(RunStatus.Succeeded);

                var own = events.Where(e => e.Contains($"{id:N}-", StringComparison.Ordinal)).ToList();

                own.Should().HaveCount(6);
                own.Should().Equal(
                    $"start {id:N}-0", $"end {id:N}-0",
                    $"start {id:N}-1", $"end {id:N}-1",
                    $"start {id:N}-2", $"end {id:N}-2");
            }

            gauge.MostAtOnce.Should().Be(2, "the two runs overlap, and no run has two actions out");
        });
    }

    /// <summary>
    /// An action that throws, beside three that succeed.
    /// </summary>
    /// <remarks>
    /// The handler's exception is turned into a Failed outcome inside the execution, as it was
    /// before there was a bound, so this guards that and does not depend on the bound. The failure
    /// outside the handler is the next test.
    ///
    /// Seeded on its last attempt, so the failure is final and no retry is left behind for a later
    /// test to drain.
    /// </remarks>
    [Fact]
    public async Task An_action_that_throws_fails_alone_beside_the_others()
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var throwing = new WorkflowActionAttempt
            {
                ActionType = "ThrowingRunner",
                Attempts = WorkflowRetryPolicy.MaxAttempts - 1,
            };

            var first = await SeedAsync(tenant, contentId, Slow(group, 200));
            var failing = await SeedAsync(tenant, contentId, throwing);
            var others = new List<Guid>
            {
                first,
                await SeedAsync(tenant, contentId, Slow(group, 200)),
                await SeedAsync(tenant, contentId, Slow(group, 200)),
            };

            await DrainAsync(NewRunner(4));

            var failed = await LoadAsync(tenant, failing, actions: 1);
            failed.Actions[0].Status.Should().Be(AttemptStatus.Failed);
            failed.Actions[0].Error.Should().Be(nameof(InvalidOperationException));

            others.Should().HaveCount(3);
            foreach (var id in others)
            {
                (await LoadAsync(tenant, id, actions: 1)).Actions[0].Status.Should().Be(AttemptStatus.Succeeded,
                    "the three beside the failure recorded their own outcome");
            }
        });
    }

    /// <summary>
    /// A run whose entry cannot be read, beside three that succeed. The failure is outside the
    /// handler, so nothing turns it into an outcome: it is logged, the attempt is left under its
    /// lease, and the pass and the other three go on.
    /// </summary>
    /// <remarks>
    /// The entry is loaded before the handler is called. Thrown on from the action's task, the
    /// failure would come out of the wait for the whole pass, and the drain here would throw.
    /// </remarks>
    [Fact]
    public async Task A_run_whose_entry_cannot_be_read_does_not_fail_the_pass_or_the_runs_beside_it()
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);
            var unreadable = await StoreContentAsync(tenant);

            var first = await SeedAsync(tenant, contentId, Slow(group, 200));
            var broken = await SeedAsync(tenant, unreadable, Slow(group, 0));
            var others = new List<Guid>
            {
                first,
                await SeedAsync(tenant, contentId, Slow(group, 200)),
                await SeedAsync(tenant, contentId, Slow(group, 200)),
            };

            await ExecuteSqlAsync(
                "update public.mt_doc_contents set data = jsonb_set(data, '{Data}', '\"not a map\"'::jsonb) "
              + $"where id = '{unreadable}'");

            try
            {
                await DrainAsync(NewRunner(4));

                others.Should().HaveCount(3);
                foreach (var id in others)
                {
                    (await LoadAsync(tenant, id, actions: 1)).Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
                }

                var attempt = (await LoadAsync(tenant, broken, actions: 1)).Actions[0];
                attempt.Status.Should().Be(AttemptStatus.Running, "nothing was recorded, so it waits out its lease");
                attempt.LeasedBy.Should().NotBeNullOrEmpty();
                attempt.Attempts.Should().Be(0);

                SlowRunnerAction.Groups[group].TimesRun(attempt.IdempotencyKey).Should().Be(0,
                    "the entry is read before the handler is called");
            }
            finally
            {
                // Left in place the run would be claimed again when its lease ends, and fail again.
                await using (var session = Store.LightweightSession(tenant))
                {
                    session.Delete<WorkflowRun>(broken);
                    await session.SaveChangesAsync(CancellationToken.None);
                }

                await ExecuteSqlAsync($"delete from public.mt_doc_contents where id = '{unreadable}'");
            }
        });
    }

    /// <summary>
    /// Two runners, each with a bound of four, draining the same sixteen runs at once.
    /// </summary>
    [Fact]
    public async Task Two_runners_with_a_bound_above_one_run_each_attempt_once()
    {
        const int runs = 16;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var seeded = new List<Guid>();
            for (var i = 0; i < runs; i++)
            {
                seeded.Add(await SeedAsync(tenant, contentId, Slow(group, 20)));
            }

            await Task.WhenAll(
                Task.Run(() => DrainAsync(NewRunner(4)), Ct),
                Task.Run(() => DrainAsync(NewRunner(4)), Ct));

            var gauge = SlowRunnerAction.Groups[group];

            seeded.Should().HaveCount(runs);
            foreach (var id in seeded)
            {
                var run = await LoadAsync(tenant, id, actions: 1);
                run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
                gauge.TimesRun(run.Actions[0].IdempotencyKey).Should().Be(1);
            }
        });
    }

    /// <summary>
    /// The host stops while two actions are out, and the actions take a while to notice. The pass
    /// does not return while they are in flight, returns once they end, and neither is lost: each
    /// is still Running under this node's lease, for whoever takes it when the lease ends, or was
    /// recorded as Unknown.
    /// </summary>
    /// <remarks>
    /// Which of the two depends on whether the store still takes a write on a cancelled token, and
    /// that is the same with one action in flight as with several.
    /// </remarks>
    [Fact]
    public Task Stopping_with_actions_in_flight_waits_for_them_and_loses_none() =>
        StopWithActionsInFlightAsync(runs: 2, stopFromTheFirstAction: false);

    /// <summary>
    /// The same, with the stop arriving while the pass is still claiming: the first action to start
    /// cancels the token, with three more runs due.
    /// </summary>
    /// <remarks>
    /// The claim that is under way, or the next one, is cancelled and the pass leaves by throwing.
    /// It still has to wait for what it started. Nothing forces a claim to be under way at that
    /// moment: if all four were claimed before the first action began, this is the test above with
    /// four runs, and passes for the same reason.
    /// </remarks>
    [Fact]
    public Task Stopping_while_the_pass_is_still_claiming_waits_for_what_it_started() =>
        StopWithActionsInFlightAsync(runs: 4, stopFromTheFirstAction: true);

    private async Task StopWithActionsInFlightAsync(int runs, bool stopFromTheFirstAction)
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var gauge = new SlowRunnerAction.Gauge
            {
                HoldAfterStop = release.Task,
                OnEnter = stopFromTheFirstAction ? new Action(stopping.Cancel) : null,
            };
            SlowRunnerAction.Groups[group] = gauge;

            var seeded = new List<Guid>();
            for (var i = 0; i < runs; i++)
            {
                seeded.Add(await SeedAsync(tenant, contentId, Slow(group, 60_000)));
            }

            // More slots than runs, so a stray run from another class cannot keep one of these out.
            var pass = Task.Run(() => NewRunner(runs + 2).RunOnceAsync(stopping.Token), Ct);

            try
            {
                var wanted = stopFromTheFirstAction ? 1 : runs;
                var deadline = Stopwatch.StartNew();

                while (gauge.InFlight < wanted)
                {
                    deadline.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "the actions should have started");
                    await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
                }

                stopping.Cancel();

                // Long enough for a pass that does not wait to have come back.
                await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

                pass.IsCompleted.Should().BeFalse("an action the pass started is still running");
                gauge.InFlight.Should().BeGreaterThanOrEqualTo(wanted);

                release.SetResult();

                try
                {
                    await pass.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // Either is a pass that ended: returned, or cancelled on the way out.
                }

                gauge.InFlight.Should().Be(0, "the pass does not return while an action it started is running");

                seeded.Should().HaveCount(runs);
                foreach (var id in seeded)
                {
                    var attempt = (await LoadAsync(tenant, id, actions: 1)).Actions[0];

                    if (stopFromTheFirstAction)
                    {
                        // A run the stop reached before its claim is still waiting.
                        attempt.Status.Should().BeOneOf(AttemptStatus.Pending, AttemptStatus.Running, AttemptStatus.Unknown);
                    }
                    else
                    {
                        attempt.Status.Should().BeOneOf(AttemptStatus.Running, AttemptStatus.Unknown);
                    }

                    if (attempt.Status == AttemptStatus.Running)
                    {
                        attempt.LeasedBy.Should().NotBeNullOrEmpty();
                        attempt.LeaseExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
                        attempt.Attempts.Should().Be(0);
                    }
                }
            }
            finally
            {
                // A failure above must not leave the pass and its minute-long actions running beside
                // the classes after this one.
                stopping.Cancel();
                release.TrySetResult();

                try
                {
                    await pass.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (Exception)
                {
                    // How the pass ended was asserted on above, or is not what failed.
                }

                // Left in place they would be run again, a minute each, when the lease ends.
                await using var session = Store.LightweightSession(tenant);
                foreach (var id in seeded) session.Delete<WorkflowRun>(id);
                await session.SaveChangesAsync(CancellationToken.None);
            }
        });
    }

    private static IConfiguration Config(string? bound) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WorkflowRunner.ConcurrencyKey] = bound })
            .Build();

    private WorkflowRunner NewRunner(int bound)
    {
        var config = new ConfigurationBuilder()
            .AddConfiguration(_factory.Services.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkflowRunner.ConcurrencyKey] = bound.ToString(CultureInfo.InvariantCulture),
            })
            .Build();

        return new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            config);
    }

    private static string NewTenant() => $"conc-{Guid.NewGuid():N}"[..21];

    private static string NewGroup() => Guid.NewGuid().ToString("N");

    private static WorkflowActionAttempt Slow(string group, int delayMs) => new()
    {
        ActionType = SlowRunnerAction.ActionType,
        Parameters = new Dictionary<string, string>
        {
            [SlowRunnerAction.GroupParameter] = group,
            [SlowRunnerAction.DelayParameter] = delayMs.ToString(CultureInfo.InvariantCulture),
        },
    };

    private static async Task DrainAsync(WorkflowRunner runner)
    {
        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
    }

    /// <summary>An entry for the runner to load. Without one it skips the action and calls nothing.</summary>
    private async Task<Guid> StoreContentAsync(string tenant)
    {
        var id = Guid.NewGuid();

        await using var session = Store.LightweightSession(tenant);
        session.Store(new Content
        {
            Id = id,
            ContentType = "article",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object>(),
        });
        await session.SaveChangesAsync(Ct);

        return id;
    }

    private async Task<Guid> SeedAsync(string tenant, Guid contentId, params WorkflowActionAttempt[] attempts)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Bounded",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
        };

        for (var i = 0; i < attempts.Length; i++)
        {
            attempts[i].Ordinal = i;
            attempts[i].IdempotencyKey = $"{run.Id:N}-{i}";
            run.Actions.Add(attempts[i]);
        }

        run.Recompute();

        await using var session = Store.LightweightSession(tenant);
        session.Store(run);
        await session.SaveChangesAsync(Ct);

        return run.Id;
    }

    private async Task ExecuteSqlAsync(string sql)
    {
        await using var conn = Store.Storage.Database.CreateConnection();
        await conn.OpenAsync(CancellationToken.None);

        await using var command = conn.CreateCommand();
        command.CommandText = sql;
        (await command.ExecuteNonQueryAsync(CancellationToken.None)).Should().Be(1, "the statement is written for one row");
    }

    private async Task<WorkflowRun> LoadAsync(string tenant, Guid runId, int actions)
    {
        await using var session = Store.QuerySession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);

        run.Should().NotBeNull();
        run!.Actions.Should().HaveCount(actions);
        return run;
    }
}
