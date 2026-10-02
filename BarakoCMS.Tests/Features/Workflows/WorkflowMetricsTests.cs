using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The label values: which words they can be, and that the number of them has a ceiling.
/// </summary>
/// <remarks>
/// No host and no database. Each test builds its metrics on a registry of its own.
/// </remarks>
public class WorkflowMetricsTests
{
    private static WorkflowMetrics NewMetrics() => new(Prometheus.Metrics.NewCustomRegistry());

    [Fact]
    public void A_registered_action_type_past_the_limit_is_counted_as_other()
    {
        var metrics = NewMetrics();

        for (var i = 0; i < WorkflowMetrics.MaxActionLabels; i++)
        {
            metrics.ActionLabel($"Action{i}", registered: true).Should().Be($"Action{i}");
        }

        metrics.ActionLabel("OneTooMany", registered: true).Should().Be(WorkflowMetrics.Other);
        metrics.ActionLabel("OneTooMany", registered: true).Should().Be(WorkflowMetrics.Other, "asking again does not find room");
        metrics.ActionLabel("Action0", registered: true).Should().Be("Action0", "a type that has its label keeps it");
    }

    [Fact]
    public void An_action_type_with_no_handler_is_other_and_takes_none_of_the_limit()
    {
        var metrics = NewMetrics();

        for (var i = 0; i < WorkflowMetrics.MaxActionLabels * 2; i++)
        {
            metrics.ActionLabel($"MadeUp{i}", registered: false).Should().Be(WorkflowMetrics.Other);
        }

        metrics.ActionLabel("Email", registered: true).Should().Be("Email");
        metrics.ActionLabel(" ", registered: true).Should().Be(WorkflowMetrics.Other, "a blank label value is no label");
    }

    [Theory]
    [InlineData(WorkflowEvents.Created, "created")]
    [InlineData(WorkflowEvents.Updated, "updated")]
    [InlineData(WorkflowEvents.Deleted, "deleted")]
    [InlineData(WorkflowEvents.Published, "published")]
    [InlineData(WorkflowEvents.Unpublished, "unpublished")]
    [InlineData("transition:Approve", "transition")]
    [InlineData("transition:whatever-a-tenant-called-it", "transition")]
    [InlineData("published", "other")]
    [InlineData("", "other")]
    [InlineData("anything else", "other")]
    public void A_trigger_is_one_of_seven_label_values(string trigger, string label)
    {
        WorkflowMetrics.TriggerLabel(trigger).Should().Be(label);
    }

    [Fact]
    public void Every_attempt_status_has_a_fixed_outcome_label()
    {
        var labels = Enum.GetValues<AttemptStatus>().Select(WorkflowMetrics.OutcomeLabel).Distinct().ToList();

        labels.Should().HaveCount(6);
        labels.Should().BeEquivalentTo(["succeeded", "failed", "retried", "unknown", "skipped", "other"]);
    }

    [Fact]
    public void A_run_that_is_still_open_is_not_counted_as_finished()
    {
        var metrics = NewMetrics();

        metrics.Finished(RunStatus.Pending);
        metrics.Finished(RunStatus.Running);
        metrics.Finished(RunStatus.PartiallyFailed);

        metrics.RunsFinished.WithLabels("partially_failed").Value.Should().Be(1, "the counter does move for a run that ended");

        foreach (var status in new[] { "succeeded", "failed", "cancelled" })
        {
            metrics.RunsFinished.WithLabels(status).Value.Should().Be(0);
        }
    }

    [Fact]
    public void Queueing_nothing_counts_nothing()
    {
        var metrics = NewMetrics();

        metrics.Queued(WorkflowEvents.Published, 0);
        metrics.Queued(WorkflowEvents.Published, 2);

        metrics.RunsQueued.WithLabels("published").Value.Should().Be(2);
    }
}
