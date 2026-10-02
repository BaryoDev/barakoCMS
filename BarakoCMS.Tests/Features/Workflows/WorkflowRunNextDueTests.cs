using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <see cref="WorkflowRun.NextDueAt"/> has to say what the runner's own claim order would say, or
/// the query offers runs that cannot be claimed and hides ones that can.
/// </summary>
public class WorkflowRunNextDueTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddMinutes(10);

    [Fact]
    public void A_run_nobody_has_tried_is_due_at_once_whatever_clock_wrote_it()
    {
        var run = Run(new WorkflowActionAttempt { Ordinal = 0 });

        run.NextDueAt.Should().Be(WorkflowRun.DueAtOnce);
        WorkflowRun.DueAtOnce.Should().BeBefore(Created.AddYears(-50),
            "a node whose clock runs behind the writer's must still read the run as due");
    }

    [Fact]
    public void A_claimed_run_with_no_lease_time_is_due_at_once()
    {
        var run = Run(new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Running });

        run.NextDueAt.Should().Be(WorkflowRun.DueAtOnce);
    }

    [Fact]
    public void A_run_in_backoff_is_due_when_its_wait_ends()
    {
        var run = Run(new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Later });

        run.NextDueAt.Should().Be(Later);
    }

    [Fact]
    public void A_claimed_run_is_due_again_when_the_lease_ends()
    {
        var run = Run(new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Running, LeaseExpiresAt = Later });

        run.NextDueAt.Should().Be(Later);
    }

    [Fact]
    public void A_waiting_action_does_not_hold_up_the_one_after_it()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Later },
            new WorkflowActionAttempt { Ordinal = 1 });

        run.NextDueAt.Should().Be(WorkflowRun.DueAtOnce, "the runner skips an action in backoff and takes the next");
    }

    [Fact]
    public void Nothing_after_a_running_action_is_due_before_its_lease_ends()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Running, LeaseExpiresAt = Later },
            new WorkflowActionAttempt { Ordinal = 1 });

        run.NextDueAt.Should().Be(Later);
    }

    [Fact]
    public void An_action_in_backoff_ahead_of_a_running_one_is_due_first()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, NextAttemptAt = Created.AddMinutes(2) },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Running, LeaseExpiresAt = Later });

        run.NextDueAt.Should().Be(Created.AddMinutes(2));
    }

    [Fact]
    public void A_finished_run_is_never_due()
    {
        var run = Run(
            new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Succeeded },
            new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Failed });

        run.NextDueAt.Should().BeNull();
    }

    private static WorkflowRun Run(params WorkflowActionAttempt[] actions)
    {
        var run = new WorkflowRun { CreatedAt = Created, Actions = [.. actions] };
        run.Recompute();
        return run;
    }
}
