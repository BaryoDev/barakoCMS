using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

public class WorkflowActionAttemptRecordTests
{
    [Fact]
    public void The_first_entry_of_an_attempt_that_counted_tries_without_history_drops_the_earlier_ones()
    {
        var attempt = new WorkflowActionAttempt { Attempts = 4 };

        attempt.RecordAttempt(new WorkflowAttemptEntry { Number = 4 });

        attempt.History.Should().HaveCount(1);
        attempt.DroppedAttempts.Should().Be(3);
    }

    [Fact]
    public void The_first_try_of_a_new_attempt_drops_nothing_and_later_ones_do_not_count_again()
    {
        var attempt = new WorkflowActionAttempt();

        attempt.RecordAttempt(new WorkflowAttemptEntry { Number = 1 });
        attempt.RecordAttempt(new WorkflowAttemptEntry { Number = 2 });

        attempt.History.Should().HaveCount(2);
        attempt.DroppedAttempts.Should().Be(0);
    }
}
