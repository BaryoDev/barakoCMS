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

    [Fact]
    public async Task A_takeover_of_an_action_that_honours_the_key_runs_it_again()
    {
        var run = await TakeOverAsync(WorkflowStopHarness.Counting, attempts: 0);

        var attempt = run.Actions.Should().ContainSingle().Which;
        attempt.Status.Should().Be(AttemptStatus.Succeeded);
        WorkflowStopHarness.TimesRun(attempt).Should().Be(1, "the key is stable, so the rerun is absorbed downstream");
    }
}
