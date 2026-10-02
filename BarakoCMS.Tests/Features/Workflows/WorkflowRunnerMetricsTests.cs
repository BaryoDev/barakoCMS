using System.Text;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What the runner counts: each attempt once under its outcome, the claim, the run it finished or
/// halted, when it last completed a pass, and how much is due.
/// </summary>
/// <remarks>
/// Every test gives a runner of its own a registry of its own, on the fixture's services, and stops
/// the fixture's hosted runner while it runs. So the registry holds only what that one runner did.
///
/// That runner still claims whatever else is due in the database, which is the retries other
/// classes left behind. Those never use <see cref="MeteredRunnerAction"/>, so a series labelled
/// with it is asserted exactly. A series with no action label (claims, finished runs, halts) is
/// asserted as at least what the test's own runs add.
///
/// A test that leaves an attempt waiting on a retry stops that run before it ends, or the next test
/// would find it due.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunnerMetricsTests
{
    private const string Metered = MeteredRunnerAction.ActionType;

    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunnerMetricsTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task A_succeeded_attempt_is_counted_once_with_its_duration_its_claim_and_its_finished_run()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [Attempt(MeteredRunnerAction.Succeed)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Status.Should().Be(RunStatus.Succeeded, "the counts below are of a run that went out");

        metrics.Attempts.WithLabels(Metered, "succeeded").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "failed").Value.Should().Be(0);
        metrics.Attempts.WithLabels(Metered, "retried").Value.Should().Be(0);
        metrics.ActionDuration.WithLabels(Metered).Count.Should().Be(1);
        metrics.AttemptsClaimed.Value.Should().BeGreaterThanOrEqualTo(1);
        metrics.RunsFinished.WithLabels("succeeded").Value.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_failure_the_runner_queues_again_is_counted_as_retried_and_not_as_failed()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [Attempt(MeteredRunnerAction.Fail)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
            await StopAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Pending, "a 503 is queued for another try");
        run.Actions[0].Attempts.Should().BeGreaterThanOrEqualTo(1, "a long drain can reach its second try");

        metrics.Attempts.WithLabels(Metered, "retried").Value.Should().Be(run.Actions[0].Attempts);
        metrics.Attempts.WithLabels(Metered, "failed").Value.Should().Be(0);
        metrics.ActionDuration.WithLabels(Metered).Count.Should().Be(run.Actions[0].Attempts);
    }

    [Fact]
    public async Task A_failure_on_the_last_attempt_is_counted_as_failed_and_its_run_as_finished_failed()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var lastTry = Attempt(MeteredRunnerAction.Fail);
            lastTry.Attempts = WorkflowRetryPolicy.MaxAttempts - 1;

            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [lastTry]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Status.Should().Be(RunStatus.Failed);

        metrics.Attempts.WithLabels(Metered, "failed").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "retried").Value.Should().Be(0);
        metrics.RunsFinished.WithLabels("failed").Value.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A_halting_action_that_fails_for_good_counts_one_failure_one_halt_and_nothing_for_the_skipped_action()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var halting = Attempt(MeteredRunnerAction.Refuse);
            halting.OnFailure = WorkflowFailurePolicy.Halt;

            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [halting, Attempt(MeteredRunnerAction.Succeed)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Failed, AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);

        metrics.Attempts.WithLabels(Metered, "failed").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "succeeded").Value.Should().Be(0, "the action after the halt never ran");
        metrics.Attempts.WithLabels(Metered, "skipped").Value.Should().Be(0, "it was not attempted, so it is not an attempt");
        metrics.RunsHalted.Value.Should().BeGreaterThanOrEqualTo(1);
        metrics.RunsFinished.WithLabels("failed").Value.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// The same failure on an action left to continue. The run is not halted, and the action after
    /// it runs.
    /// </summary>
    /// <remarks>
    /// The halt counter has no action label, so a halting retry another class left behind would
    /// move it if this test's runner were the one to run its last attempt. Everything already due
    /// is drained by another runner first, which leaves only a retry coming due during this test's
    /// own drain.
    /// </remarks>
    [Fact]
    public async Task A_failure_of_an_action_set_to_continue_counts_the_next_action_and_no_halt()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await _harness.DrainAsync();

            var seeded = await _harness.SeedRunAsync(workflowId, contentId,
                [Attempt(MeteredRunnerAction.Refuse), Attempt(MeteredRunnerAction.Succeed)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Status.Should().Be(RunStatus.PartiallyFailed);

        metrics.Attempts.WithLabels(Metered, "failed").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "succeeded").Value.Should().Be(1);
        metrics.RunsFinished.WithLabels("partially_failed").Value.Should().BeGreaterThanOrEqualTo(1);
        metrics.RunsHalted.Value.Should().Be(0, "an action set to continue stops nothing");
    }

    /// <summary>
    /// A timeout halts as a failure does: it does not say whether the step happened.
    /// </summary>
    [Fact]
    public async Task A_halting_action_that_times_out_is_counted_as_unknown_and_as_a_halt()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var halting = Attempt(MeteredRunnerAction.TimeOut);
            halting.OnFailure = WorkflowFailurePolicy.Halt;

            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [halting, Attempt(MeteredRunnerAction.Succeed)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Unknown, AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);

        metrics.Attempts.WithLabels(Metered, "unknown").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "succeeded").Value.Should().Be(0, "the action after the halt never ran");
        metrics.RunsHalted.Value.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// A run that was told to stop while it waited. The runner cancels what is left in place of
    /// claiming it, and that is the only place such a run is counted as finished.
    /// </summary>
    [Fact]
    public async Task A_stopped_run_the_runner_cancels_in_place_of_claiming_is_counted_as_finished_cancelled()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(
                workflowId, contentId, [Attempt(MeteredRunnerAction.Succeed)], cancelledAt: DateTimeOffset.UtcNow);
            seeded.Status.Should().Be(RunStatus.Pending, "the run has to be unfinished for the runner to be the one that ends it");

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Status.Should().Be(RunStatus.Cancelled);
        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Cancelled);

        metrics.RunsFinished.WithLabels("cancelled").Value.Should().BeGreaterThanOrEqualTo(1);
        metrics.Attempts.WithLabels(Metered, "succeeded").Value.Should().Be(0, "a cancelled action never went out");
        metrics.ActionDuration.WithLabels(Metered).Count.Should().Be(0);
    }

    [Fact]
    public async Task A_timeout_is_counted_as_unknown()
    {
        var (runner, metrics, _) = NewRunner();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [Attempt(MeteredRunnerAction.TimeOut)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Unknown);

        metrics.Attempts.WithLabels(Metered, "unknown").Value.Should().Be(1);
        metrics.Attempts.WithLabels(Metered, "retried").Value.Should().Be(0, "a timeout is never queued again on its own");
    }

    /// <summary>
    /// An action type is text stored on the workflow. With no handler for it, the type must not
    /// become a label value, or a tenant could add a series for every name it can think of.
    /// </summary>
    [Fact]
    public async Task An_action_type_with_no_handler_is_counted_as_other_and_its_name_is_not_published()
    {
        var (runner, metrics, registry) = NewRunner();
        var madeUp = WorkflowStopHarness.NewName("TenantChoseThis");
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Waiting(madeUp, due: true)]);

            await DrainAsync(runner);

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);

        metrics.Attempts.WithLabels(WorkflowMetrics.Other, "failed").Value.Should().BeGreaterThanOrEqualTo(1);
        metrics.ActionDuration.WithLabels(WorkflowMetrics.Other).Count.Should().Be(0, "nothing was called");

        var published = await ExportAsync(registry);
        published.Should().Contain("barakocms_workflow_attempts_total", "the export has to hold the counter for the next line to mean anything");
        published.Should().NotContain(madeUp);
    }

    /// <summary>
    /// A run in a tenant of its own, with a workflow name, a content type and a transition only this
    /// test uses. None of them may appear in what the registry publishes.
    /// </summary>
    /// <remarks>
    /// The entry does not exist, so the attempt is Skipped. That is the one outcome the other tests
    /// here do not reach.
    /// </remarks>
    [Fact]
    public async Task Nothing_a_tenant_chose_is_published_and_an_attempt_skipped_for_missing_content_has_no_duration()
    {
        var (runner, metrics, registry) = NewRunner();
        var tenant = $"metrics-{Guid.NewGuid():N}"[..24];
        var workflowName = WorkflowStopHarness.NewName("PayrollExport");
        var contentType = WorkflowStopHarness.NewName("payslip");
        var transition = WorkflowStopHarness.NewName("Approve");
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = NewRun(DateTimeOffset.UtcNow, due: true);
            seeded.WorkflowName = workflowName;
            seeded.ContentType = contentType;
            seeded.TriggerEvent = WorkflowEvents.TransitionPrefix + transition;
            await StoreAsync(tenant, seeded);

            await DrainAsync(runner);

            run = await LoadAsync(tenant, seeded.Id);
        });

        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Skipped, "the entry it names was never stored");

        metrics.Attempts.WithLabels(Metered, "skipped").Value.Should().Be(1);
        metrics.ActionDuration.WithLabels(Metered).Count.Should().Be(0, "nothing was called");

        var published = await ExportAsync(registry);
        published.Should().Contain("barakocms_workflow_attempts_total");

        foreach (var chosen in new[] { tenant, workflowName, contentType, transition, run.Id.ToString(), run.ContentId.ToString() })
        {
            published.Should().NotContain(chosen);
        }
    }

    /// <summary>
    /// The loop, started as the host starts it: it publishes when it completed a pass and when it
    /// counted the due runs.
    /// </summary>
    [Fact]
    public async Task The_running_loop_publishes_when_it_last_completed_a_pass_and_when_it_counted_the_backlog()
    {
        var (runner, metrics, _) = NewRunner();
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            metrics.LastPass.Value.Should().Be(0, "nothing has run yet");

            await runner.StartAsync(CancellationToken.None);

            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while ((metrics.LastPass.Value == 0 || metrics.BacklogMeasured.Value == 0) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(100, Ct);
                }
            }
            finally
            {
                await runner.StopAsync(CancellationToken.None);
            }
        });

        metrics.LastPass.Value.Should().BeGreaterThanOrEqualTo(before, "a pass completed after the runner started");
        metrics.BacklogMeasured.Value.Should().BeGreaterThanOrEqualTo(before, "the first loop counts the due runs");
    }

    /// <summary>
    /// Three runs in a tenant of their own, one of them queued three days ago, counted before and
    /// after they come due.
    /// </summary>
    /// <remarks>
    /// The difference between the two counts is asserted, not the count: retries other classes left
    /// behind can be due in other partitions at either moment.
    /// </remarks>
    [Fact]
    public async Task The_backlog_gauges_count_the_runs_that_are_due_and_age_the_oldest()
    {
        var (runner, metrics, _) = NewRunner();
        var tenant = $"backlog-{Guid.NewGuid():N}"[..24];
        var threeDays = TimeSpan.FromDays(3);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            (await runner.MeasureBacklogAsync(Ct)).Should().BeFalse("no pass has listed the partitions yet");
            metrics.BacklogMeasured.Value.Should().Be(0);

            var runs = new[]
            {
                NewRun(DateTimeOffset.UtcNow - threeDays, due: false),
                NewRun(DateTimeOffset.UtcNow, due: false),
                NewRun(DateTimeOffset.UtcNow, due: false),
            };
            foreach (var run in runs) await StoreAsync(tenant, run);

            await DrainAsync(runner);

            (await runner.MeasureBacklogAsync(Ct)).Should().BeTrue();
            var waiting = metrics.DueRuns.Value;

            foreach (var run in runs) await MakeDueAsync(tenant, run.Id);

            (await runner.MeasureBacklogAsync(Ct)).Should().BeTrue();

            metrics.DueRuns.Value.Should().BeGreaterThanOrEqualTo(waiting + 3);
            metrics.OldestDueRunAge.Value.Should().BeGreaterThanOrEqualTo(threeDays.TotalSeconds - 60);
            metrics.BacklogMeasured.Value.Should().BeGreaterThan(0);

            await DrainAsync(runner);

            (await runner.MeasureBacklogAsync(Ct)).Should().BeTrue();
            metrics.DueRuns.Value.Should().BeLessThanOrEqualTo(waiting + 2, "the three are no longer due once they have run");
        });
    }

    /// <summary>
    /// A count that cannot finish: nothing is published and nothing is thrown, so the loop that
    /// called it goes on to its next pass.
    /// </summary>
    [Fact]
    public async Task A_backlog_count_that_cannot_finish_publishes_nothing_and_does_not_throw()
    {
        var (runner, metrics, _) = NewRunner();
        var tenant = $"backlog-{Guid.NewGuid():N}"[..24];

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var parked = NewRun(DateTimeOffset.UtcNow, due: false);
            await StoreAsync(tenant, parked);

            await DrainAsync(runner);

            (await runner.MeasureBacklogAsync(Ct)).Should().BeTrue();
            var measuredAt = metrics.BacklogMeasured.Value;
            var waiting = metrics.DueRuns.Value;
            measuredAt.Should().BeGreaterThan(0);

            // Due now, so a count that did finish would publish at least one more than the last.
            await MakeDueAsync(tenant, parked.Id);

            using var stopped = new CancellationTokenSource();
            await stopped.CancelAsync();

            var measured = await runner.MeasureBacklogAsync(stopped.Token);

            measured.Should().BeFalse("the partition this test seeded has to be read, and the read is cancelled");
            metrics.BacklogMeasured.Value.Should().Be(measuredAt, "the time of the last count that finished is how a reader sees the numbers are old");
            metrics.DueRuns.Value.Should().Be(waiting, "the last numbers stay");

            await StopAsync(tenant, parked.Id);
        });
    }

    /// <summary>
    /// <c>Workflows:BacklogIntervalSeconds</c> at 0: the loop completes passes and never counts.
    /// </summary>
    [Fact]
    public async Task With_the_backlog_interval_at_zero_the_loop_completes_passes_and_never_counts_the_backlog()
    {
        var (runner, metrics, _) = NewRunner(backlogIntervalSeconds: 0);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await runner.StartAsync(CancellationToken.None);

            try
            {
                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while (metrics.LastPass.Value == 0 && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(100, Ct);
                }

                // The count follows the pass in the same turn of the loop. Long enough for it to
                // have been made if it were going to be.
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
            finally
            {
                await runner.StopAsync(CancellationToken.None);
            }
        });

        metrics.LastPass.Value.Should().BeGreaterThan(0, "the loop ran, so the line below is about the setting");
        metrics.BacklogMeasured.Value.Should().Be(0);
    }

    /// <summary>
    /// A count that keeps failing warns once, and a count that succeeds starts the next streak.
    /// </summary>
    [Fact]
    public async Task A_failing_backlog_count_warns_once_per_streak_and_a_count_that_succeeds_ends_the_streak()
    {
        var (runner, _, _) = NewRunner();

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await DrainAsync(runner);

            runner.NoteBacklogFailure("TimeoutException").Should().BeTrue("the first failure is logged");
            runner.NoteBacklogFailure("TimeoutException").Should().BeFalse("the second one within five minutes is not");

            (await runner.MeasureBacklogAsync(Ct)).Should().BeTrue();

            runner.NoteBacklogFailure("TimeoutException").Should().BeTrue("a failure after a count that worked is a new streak");
        });
    }

    private (WorkflowRunner Runner, WorkflowMetrics Metrics, CollectorRegistry Registry) NewRunner(
        int? backlogIntervalSeconds = null)
    {
        var registry = Prometheus.Metrics.NewCustomRegistry();
        var metrics = new WorkflowMetrics(registry);

        var config = _factory.Services.GetRequiredService<IConfiguration>();
        if (backlogIntervalSeconds is { } seconds)
        {
            config = new ConfigurationBuilder()
                .AddConfiguration(config)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [WorkflowRunner.BacklogIntervalKey] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })
                .Build();
        }

        var runner = new WorkflowRunner(
            _factory.Services,
            _factory.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            config)
        {
            Metrics = metrics,
        };

        return (runner, metrics, registry);
    }

    private static WorkflowActionAttempt Attempt(string mode, bool due = true)
    {
        var attempt = WorkflowStopHarness.Waiting(Metered, due);
        attempt.Parameters[MeteredRunnerAction.ModeParameter] = mode;
        return attempt;
    }

    private static WorkflowRun NewRun(DateTimeOffset createdAt, bool due)
    {
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Metered",
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
            TriggeringEventSequence = 1,
            CreatedAt = createdAt,
        };

        var attempt = Attempt(MeteredRunnerAction.Succeed, due);
        attempt.Ordinal = 0;
        attempt.IdempotencyKey = $"{run.Id:N}-0";
        run.Actions.Add(attempt);

        run.Recompute();
        return run;
    }

    private async Task StoreAsync(string tenant, WorkflowRun run)
    {
        await using var session = _harness.Store.LightweightSession(tenant);
        session.Store(run);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<WorkflowRun> LoadAsync(string tenant, Guid runId)
    {
        await using var session = _harness.Store.QuerySession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);

        run.Should().NotBeNull();
        return run!;
    }

    private async Task MakeDueAsync(string tenant, Guid runId)
    {
        await using var session = _harness.Store.LightweightSession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);
        run.Should().NotBeNull();

        foreach (var attempt in run!.Actions.Where(a => a.Status == AttemptStatus.Pending))
        {
            attempt.NextAttemptAt = null;
        }

        run.Recompute();
        session.Update(run);
        await session.SaveChangesAsync(Ct);
    }

    private Task StopAsync(Guid runId) => StopAsync(null, runId);

    /// <summary>Stops a run this class left waiting, so a later test's runner does not find it due.</summary>
    /// <param name="tenant">Null for the default tenant, where the harness seeds.</param>
    private async Task StopAsync(string? tenant, Guid runId)
    {
        await using var session = tenant is null
            ? _harness.Store.LightweightSession()
            : _harness.Store.LightweightSession(tenant);
        var run = await session.LoadAsync<WorkflowRun>(runId, Ct);
        run.Should().NotBeNull();

        run!.Cancel(DateTimeOffset.UtcNow);
        session.Update(run);
        await session.SaveChangesAsync(Ct);
    }

    private static async Task DrainAsync(WorkflowRunner runner)
    {
        var polls = 0;
        while (await runner.RunOnceAsync(Ct))
        {
            (++polls).Should().BeLessThan(200, "the runner should drain rather than find work forever");
        }
    }

    private static async Task<string> ExportAsync(CollectorRegistry registry)
    {
        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, Ct);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
