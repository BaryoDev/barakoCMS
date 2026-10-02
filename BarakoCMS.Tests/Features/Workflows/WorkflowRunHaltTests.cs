using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What an action set to halt does to the run record: when the run is next due, what is skipped
/// once the action fails for good, how the run then reads, and what a retry queues again.
/// </summary>
public class WorkflowRunHaltTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddMinutes(10);

    /// <summary>
    /// The twin of A_waiting_action_does_not_hold_up_the_one_after_it in WorkflowRunNextDueTests,
    /// with the one difference being the policy.
    /// </summary>
    [Fact]
    public void A_waiting_action_set_to_halt_holds_up_the_one_after_it()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Later, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 1 });

        run.NextDueAt.Should().Be(Later, "nothing after a halting action can be claimed until it has succeeded");
    }

    [Fact]
    public void An_action_in_backoff_ahead_of_a_waiting_halting_one_is_still_due_first()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Created.AddMinutes(2) },
            new WorkflowActionAttempt { Ordinal = 1, NextAttemptAt = Later, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 2 });

        run.NextDueAt.Should().Be(Created.AddMinutes(2));
    }

    [Fact]
    public void Halting_skips_every_waiting_action_after_the_failed_one_and_nothing_else()
    {
        var finished = Created.AddMinutes(1);
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Later },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Failed, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 2 },
            new WorkflowActionAttempt { Ordinal = 3, Status = AttemptStatus.Succeeded, CompletedAt = finished },
            new WorkflowActionAttempt { Ordinal = 4, NextAttemptAt = Later });

        run.HaltAfter(run.Actions[1], Later).Should().Be(2);

        run.Actions.Should().HaveCount(5);
        run.Actions.Select(a => a.Status).Should().Equal(
            AttemptStatus.Pending, AttemptStatus.Failed, AttemptStatus.Skipped, AttemptStatus.Succeeded, AttemptStatus.Skipped);
        run.Actions.Select(a => a.HaltedBy).Should().Equal(new int?[] { null, null, 1, null, 1 });

        run.Actions[0].NextAttemptAt.Should().Be(Later, "an action before the failed one keeps its own retry");
        run.Actions[3].CompletedAt.Should().Be(finished);

        run.Actions[4].Error.Should().Be(WorkflowRun.SkippedAfterHalt);
        run.Actions[4].NextAttemptAt.Should().BeNull();
        run.Actions[4].CompletedAt.Should().Be(Later);
        run.Actions[4].Attempts.Should().Be(0);
    }

    /// <summary>
    /// A timeout halts as well. It is not known whether the step happened, and the steps after it
    /// would run as though it had.
    /// </summary>
    [Theory]
    [InlineData(AttemptStatus.Failed)]
    [InlineData(AttemptStatus.Unknown)]
    public void A_halting_action_that_ended_failed_or_unknown_skips_what_is_left(AttemptStatus ended)
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = ended, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 1 });

        run.HaltAfter(run.Actions[0], Later).Should().Be(1);

        run.Actions.Should().HaveCount(2);
        run.Actions[1].Status.Should().Be(AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);
    }

    /// <summary>
    /// Pending is a halting action queued for another try. Skipped is the content having gone.
    /// Neither is a failure, so the action after it is left waiting.
    /// </summary>
    [Theory]
    [InlineData(AttemptStatus.Pending, WorkflowFailurePolicy.Halt)]
    [InlineData(AttemptStatus.Running, WorkflowFailurePolicy.Halt)]
    [InlineData(AttemptStatus.Succeeded, WorkflowFailurePolicy.Halt)]
    [InlineData(AttemptStatus.Skipped, WorkflowFailurePolicy.Halt)]
    [InlineData(AttemptStatus.Cancelled, WorkflowFailurePolicy.Halt)]
    [InlineData(AttemptStatus.Failed, WorkflowFailurePolicy.Continue)]
    [InlineData(AttemptStatus.Unknown, WorkflowFailurePolicy.Continue)]
    public void Nothing_is_skipped_unless_a_halting_action_failed(AttemptStatus status, WorkflowFailurePolicy policy)
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = status, OnFailure = policy, LeaseExpiresAt = Later },
            new WorkflowActionAttempt { Ordinal = 1 });

        run.HaltAfter(run.Actions[0], Later).Should().Be(0);

        run.Actions.Should().HaveCount(2);
        run.Actions[1].Status.Should().Be(AttemptStatus.Pending);
        run.Actions[1].HaltedBy.Should().BeNull();
    }

    /// <summary>
    /// A skipped action counts towards a run that succeeded only when it was skipped because the
    /// content went. Skipped behind a failure, it is part of what did not happen.
    /// </summary>
    [Theory]
    [InlineData(AttemptStatus.Failed, RunStatus.Failed)]
    [InlineData(AttemptStatus.Unknown, RunStatus.Failed)]
    public void A_run_halted_at_its_first_action_reads_failed_and_is_finished(AttemptStatus ended, RunStatus expected)
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = ended, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 1 },
            new WorkflowActionAttempt { Ordinal = 2 });

        run.HaltAfter(run.Actions[0], Later).Should().Be(2);
        run.Recompute();

        run.Status.Should().Be(expected, "nothing of the run went out");
        run.NextDueAt.Should().BeNull("a halted run has nothing left to claim, so the due query must not offer it");
        run.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void A_run_halted_after_an_action_that_succeeded_reads_partially_failed()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Succeeded },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Failed, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 2 });

        run.HaltAfter(run.Actions[1], Later).Should().Be(1);
        run.Recompute();

        run.Status.Should().Be(RunStatus.PartiallyFailed);
        run.NextDueAt.Should().BeNull();
    }

    [Fact]
    public void An_action_skipped_because_the_content_went_still_counts_towards_success()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Skipped },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Succeeded });

        run.Status.Should().Be(RunStatus.Succeeded);
    }

    /// <summary>
    /// Only what that failure skipped. An action skipped because the content went stays skipped.
    /// </summary>
    [Fact]
    public void Resuming_queues_the_actions_that_failure_skipped_and_no_others()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Failed, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 1 },
            new WorkflowActionAttempt { Ordinal = 2, Status = AttemptStatus.Skipped, Error = "The content no longer exists.", CompletedAt = Created },
            new WorkflowActionAttempt { Ordinal = 3 });

        run.HaltAfter(run.Actions[0], Later).Should().Be(2);

        run.ResumeAfter(0).Should().Be(2);

        run.Actions.Should().HaveCount(4);
        run.Actions.Select(a => a.Status).Should().Equal(
            AttemptStatus.Failed, AttemptStatus.Pending, AttemptStatus.Skipped, AttemptStatus.Pending);
        run.Actions.Select(a => a.HaltedBy).Should().Equal(new int?[] { null, null, null, null });

        run.Actions[1].Error.Should().BeNull();
        run.Actions[1].CompletedAt.Should().BeNull();
        run.Actions[2].Error.Should().Be("The content no longer exists.");
        run.Actions[2].CompletedAt.Should().Be(Created);

        run.ResumeAfter(0).Should().Be(0, "there is nothing left that this failure skipped");
    }

    [Fact]
    public void Resuming_after_another_action_leaves_the_skipped_ones_skipped()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Failed },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Failed, OnFailure = WorkflowFailurePolicy.Halt },
            new WorkflowActionAttempt { Ordinal = 2 });

        run.HaltAfter(run.Actions[1], Later).Should().Be(1);

        run.ResumeAfter(0).Should().Be(0);

        run.Actions.Should().HaveCount(3);
        run.Actions[2].Status.Should().Be(AttemptStatus.Skipped);
        run.Actions[2].HaltedBy.Should().Be(1);
    }

    private static WorkflowRun Run(params WorkflowActionAttempt[] actions)
    {
        var run = new WorkflowRun { CreatedAt = Created, Actions = [.. actions] };
        run.Recompute();
        return run;
    }
}
