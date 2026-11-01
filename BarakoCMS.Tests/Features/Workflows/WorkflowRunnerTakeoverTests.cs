using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An attempt left Running past its lease is a node that died or hung. The takeover counts as a try,
/// so an action that keeps killing its node reaches the cap, and an action that cannot tell a second
/// call from the first is not called again.
/// </summary>
/// <remarks>
/// Each test seeds the abandoned attempt with the hosted runner paused and drains with a runner of
/// its own, so the only claim is the one under test.
/// </remarks>
[Collection("Integration1")]
public class WorkflowRunnerTakeoverTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunnerTakeoverTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static WorkflowActionAttempt Abandoned(string type, int attempts) => new()
    {
        ActionType = type,
        Status = AttemptStatus.Running,
        LeasedBy = WorkflowStopHarness.OtherNode,
        LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        Attempts = attempts,
    };

    private async Task<WorkflowRun> TakeOverAsync(string type, int attempts)
    {
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun seeded = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            seeded = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [Abandoned(type, attempts)]);
            await _harness.DrainAsync();
        });

        return await _harness.LoadRunAsync(seeded.Id);
    }

    [Fact]
    public async Task A_takeover_counts_as_an_attempt()
    {
        var run = await TakeOverAsync(WorkflowStopHarness.Counting, attempts: 1);

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Succeeded);
        attempt.Attempts.Should().Be(3, "the try that died, counted when it was taken over, and the one that finished");
        attempt.History.Should().Contain(e => e.Outcome == AttemptStatus.Unknown);
        WorkflowStopHarness.TimesRun(attempt).Should().Be(1);
    }

    [Fact]
    public async Task An_action_that_keeps_losing_its_lease_stops_at_the_cap_without_running_again()
    {
        var run = await TakeOverAsync(WorkflowStopHarness.Counting, attempts: WorkflowRetryPolicy.MaxAttempts - 1);

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Unknown);
        attempt.Attempts.Should().Be(WorkflowRetryPolicy.MaxAttempts);
        attempt.LeasedBy.Should().BeNull();
        attempt.CompletedAt.Should().NotBeNull();
        run.NextDueAt.Should().BeNull("a run that stopped is not offered to the runner again");
        WorkflowStopHarness.TimesRun(attempt).Should().Be(0);
    }

    [Fact]
    public async Task A_takeover_of_an_action_that_cannot_dedupe_ends_unknown_and_sends_nothing_more()
    {
        var run = await TakeOverAsync("SendOnceRunner", attempts: 0);

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Unknown);
        attempt.Attempts.Should().Be(1, "the try that was lost, counted once");
        attempt.History.Should().ContainSingle().Which.Outcome.Should().Be(AttemptStatus.Unknown);
        SendOnceRunnerAction.RunsByKey.GetValueOrDefault(attempt.IdempotencyKey).Should().Be(0);
    }

    private async Task<WorkflowRun> DrainSeededAsync(
        Guid contentId, IReadOnlyList<WorkflowActionAttempt> attempts, Func<WorkflowRun, Task>? afterSeed = null)
    {
        WorkflowRun seeded = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            seeded = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, attempts);
            if (afterSeed is not null) await afterSeed(seeded);
            await _harness.DrainAsync();
        });

        return await _harness.LoadRunAsync(seeded.Id);
    }

    [Fact]
    public async Task A_rerun_after_a_takeover_keeps_the_attempt_number_its_marker_was_written_under()
    {
        var contentId = await _harness.StoreContentAsync(new Dictionary<string, object> { ["note"] = "before" });
        var update = Abandoned("UpdateField", attempts: 0);
        update.Parameters = new Dictionary<string, string> { ["Field"] = "note", ["Value"] = "after" };

        var run = await DrainSeededAsync(contentId, [update], async seeded =>
        {
            await using var session = _harness.Store.LightweightSession();
            session.Store(new WorkflowFieldApplyMarker { Key = seeded.Actions[0].IdempotencyKey, Attempt = 1, AppliedAt = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync(WorkflowStopHarness.Ct);
        });

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Succeeded);
        attempt.Attempts.Should().Be(2, "the lost try is counted, so the cap and the history see it");
        attempt.Takeovers.Should().Be(1);

        await using var query = _harness.Store.QuerySession();
        var content = await query.LoadAsync<Content>(contentId, WorkflowStopHarness.Ct);
        content!.Data["note"].ToString().Should().Be("before", "the marker says attempt 1 already applied it, so the rerun leaves it alone");
    }

    [Fact]
    public async Task A_halt_policy_email_taken_over_skips_the_action_behind_it()
    {
        var contentId = await _harness.StoreContentAsync();
        var email = Abandoned("Email", attempts: 0);
        email.OnFailure = WorkflowFailurePolicy.Halt;

        var run = await DrainSeededAsync(contentId, [email, WorkflowStopHarness.Waiting(due: true)]);

        run.Actions[0].Status.Should().Be(AttemptStatus.Unknown);
        run.Actions[0].Error.Should().Contain("not known whether it finished", "the email is not rerun, so Email is built in the harness and refuses a second run");
        run.Actions[1].Status.Should().Be(AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);
    }

    [Fact]
    public async Task A_run_ended_by_a_takeover_tells_the_failure_handlers_once()
    {
        var contentId = await _harness.StoreContentAsync();

        var run = await DrainSeededAsync(contentId, [Abandoned("SendOnceRunner", attempts: 0)]);

        run.Status.Should().Be(RunStatus.Failed);
        RecordingFailureHandler.Told.GetValueOrDefault(run.Id).Should().Be(1);
    }

    [Fact]
    public async Task A_failure_handler_that_blocks_does_not_stop_the_next_claim()
    {
        var contentId = await _harness.StoreContentAsync();
        var hold = new TaskCompletionSource();
        WorkflowRun first = null!, second = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            first = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [Abandoned("SendOnceRunner", attempts: 0)]);
            second = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [Abandoned("SendOnceRunner", attempts: 0)]);
            RecordingFailureHandler.Hold = hold.Task;

            try
            {
                var pass = _harness.NewRunner().RunOnceAsync(WorkflowStopHarness.Ct);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

                while (!(RecordingFailureHandler.Told.ContainsKey(first.Id) && RecordingFailureHandler.Told.ContainsKey(second.Id))
                    && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(50, WorkflowStopHarness.Ct);
                }

                RecordingFailureHandler.Told.Should().ContainKey(first.Id);
                RecordingFailureHandler.Told.Should().ContainKey(second.Id, "the second run was claimed while the first handler was still blocked");
                hold.SetResult();
                await pass;
            }
            finally
            {
                RecordingFailureHandler.Hold = null;
                hold.TrySetResult();
            }
        });
    }

    [Fact]
    public async Task An_undo_that_lost_its_lease_is_counted_and_ends_unknown_when_it_cannot_dedupe()
    {
        var contentId = await _harness.StoreContentAsync();
        var done = new WorkflowActionAttempt
        {
            ActionType = WorkflowStopHarness.Counting,
            Status = AttemptStatus.Succeeded,
            Attempts = 1,
            CompletedAt = DateTimeOffset.UtcNow,
            Compensate = new WorkflowCompensation { Type = "SendOnceRunner" },
            Compensation = new CompensationState
            {
                Status = CompensationStatus.Running,
                IdempotencyKey = $"undo-{Guid.NewGuid():N}",
                LeasedBy = WorkflowStopHarness.OtherNode,
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            },
        };

        var run = await DrainSeededAsync(contentId, [done]);

        var undo = run.Actions.Should().ContainSingle().Which.Compensation!;
        undo.Status.Should().Be(CompensationStatus.Unknown);
        undo.Attempts.Should().Be(1);
        undo.Takeovers.Should().Be(1);
        SendOnceRunnerAction.RunsByKey.GetValueOrDefault(undo.IdempotencyKey).Should().Be(0);
    }

    [Fact]
    public async Task A_takeover_of_an_action_that_honours_the_key_runs_it_again()
    {
        var run = await TakeOverAsync(WorkflowStopHarness.Counting, attempts: 0);

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Succeeded);
        WorkflowStopHarness.TimesRun(attempt).Should().Be(1, "the key is stable, so the rerun is absorbed downstream");
    }
}
