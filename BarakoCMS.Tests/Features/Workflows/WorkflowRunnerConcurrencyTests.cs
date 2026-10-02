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
/// and the number of runs, with half the gap between the serial time and the best case as room for
/// a loaded machine.
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
    /// limit asserted is halfway between that and the best case of two.
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
            var limit = best + ((serial - best) / 2);

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
    /// One tenant with eight due runs, one with two, and four slots: the first pass runs two of each.
    /// </summary>
    /// <remarks>
    /// Filling the slots from the first tenant that has work gives it all four, and the tenant with
    /// two waits a pass for every four runs the other has queued.
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
            executed[prefix + "-a"].Should().Be(2);
            executed[prefix + "-b"].Should().Be(2, "the slots of a pass are handed out a round of the tenants at a time");

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
    /// An action that throws, in the same pass as three that succeed.
    /// </summary>
    /// <remarks>
    /// Seeded on its last attempt, so the failure is final and no retry is left behind for a later
    /// test to drain.
    /// </remarks>
    [Fact]
    public async Task An_action_that_throws_fails_alone_beside_the_others_of_its_pass()
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

            (await NewRunner(4).RunOnceAsync(Ct)).Should().BeTrue("four runs are due");

            var failed = await LoadAsync(tenant, failing, actions: 1);
            failed.Actions[0].Status.Should().Be(AttemptStatus.Failed);
            failed.Actions[0].Error.Should().Be(nameof(InvalidOperationException));

            others.Should().HaveCount(3);
            foreach (var id in others)
            {
                (await LoadAsync(tenant, id, actions: 1)).Actions[0].Status.Should().Be(AttemptStatus.Succeeded,
                    "one pass took all four, and the three beside the failure recorded their own outcome");
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
    /// The host stops while two actions are out. The pass waits for both to end, and neither is
    /// lost: each is still Running under this node's lease, for whoever takes it when the lease
    /// ends, or was recorded as Unknown.
    /// </summary>
    /// <remarks>
    /// Which of the two depends on whether the store still takes a write on a cancelled token, and
    /// that is the same with one action in flight as with several.
    /// </remarks>
    [Fact]
    public async Task Stopping_with_actions_in_flight_waits_for_them_and_loses_none()
    {
        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(NewRunner(1));

            var tenant = NewTenant();
            var group = NewGroup();
            var contentId = await StoreContentAsync(tenant);

            var seeded = new List<Guid>
            {
                await SeedAsync(tenant, contentId, Slow(group, 60_000)),
                await SeedAsync(tenant, contentId, Slow(group, 60_000)),
            };

            try
            {
                using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                var pass = Task.Run(() => NewRunner(2).RunOnceAsync(stopping.Token), Ct);

                var deadline = Stopwatch.StartNew();
                while (!(SlowRunnerAction.Groups.TryGetValue(group, out var started) && started.InFlight == 2))
                {
                    deadline.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "both actions should have started");
                    await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
                }

                stopping.Cancel();

                try
                {
                    await pass.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    // Either is a pass that ended: returned, or cancelled on the way out.
                }

                SlowRunnerAction.Groups[group].InFlight.Should().Be(0, "the pass does not return while an action it started is running");

                seeded.Should().HaveCount(2);
                foreach (var id in seeded)
                {
                    var attempt = (await LoadAsync(tenant, id, actions: 1)).Actions[0];
                    attempt.Status.Should().BeOneOf(AttemptStatus.Running, AttemptStatus.Unknown);

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

    private async Task<WorkflowRun> LoadAsync(string tenant, Guid runId, int actions)
    {
        await using var session = Store.QuerySession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);

        run.Should().NotBeNull();
        run!.Actions.Should().HaveCount(actions);
        return run;
    }
}
