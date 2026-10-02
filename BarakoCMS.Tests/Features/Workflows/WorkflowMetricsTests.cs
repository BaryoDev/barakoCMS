using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    public void A_failed_or_unknown_attempt_halted_its_run_only_when_an_action_was_skipped_behind_it()
    {
        Run(AttemptStatus.Failed, skippedBehind: 0).Should().BeTrue();
        Run(AttemptStatus.Unknown, skippedBehind: 0).Should().BeTrue("a timeout halts as a failure does");

        Run(AttemptStatus.Failed, skippedBehind: null).Should().BeFalse("the action after it was skipped because the content went");
        Run(AttemptStatus.Succeeded, skippedBehind: 0).Should().BeFalse("an attempt that succeeded halted nothing");
        Run(AttemptStatus.Pending, skippedBehind: 0).Should().BeFalse("an attempt queued again has not failed yet");

        static bool Run(AttemptStatus first, int? skippedBehind)
        {
            var run = new WorkflowRun
            {
                Actions =
                [
                    new WorkflowActionAttempt { Ordinal = 0, Status = first },
                    new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Skipped, HaltedBy = skippedBehind },
                ],
            };

            return WorkflowMetrics.HaltedTheRun(run, run.Actions[0]);
        }
    }

    [Fact]
    public void A_failed_attempt_with_a_later_action_that_ran_did_not_halt_its_run()
    {
        var run = new WorkflowRun
        {
            Actions =
            [
                new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Failed },
                new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Succeeded },
            ],
        };

        WorkflowMetrics.HaltedTheRun(run, run.Actions[0]).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("30", 30)]
    [InlineData("5", 5)]
    [InlineData("3600", 3600)]
    public void The_backlog_interval_is_thirty_seconds_unless_it_is_set(string? configured, int seconds)
    {
        WorkflowRunner.ReadBacklogInterval(Config(configured)).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void A_backlog_interval_of_zero_switches_the_count_off()
    {
        WorkflowRunner.ReadBacklogInterval(Config("0")).Should().BeNull();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("3601")]
    public void A_backlog_interval_out_of_range_is_refused_by_name(string configured)
    {
        var read = () => WorkflowRunner.ReadBacklogInterval(Config(configured));

        read.Should().Throw<InvalidOperationException>().WithMessage($"*{WorkflowRunner.BacklogIntervalKey}*");
    }

    /// <summary>
    /// The warning for a count that failed: one line for the streak, the reason it was given, and
    /// no exception, so no stack trace and no message from the database.
    /// </summary>
    [Fact]
    public void A_failing_backlog_count_writes_one_warning_with_the_reason_and_no_exception()
    {
        var logger = new CapturingLogger();
        var runner = new WorkflowRunner(new ServiceCollection().BuildServiceProvider(), logger, Config(null));

        runner.NoteBacklogFailure("NpgsqlException").Should().BeTrue();
        runner.NoteBacklogFailure("NpgsqlException").Should().BeFalse();
        runner.NoteBacklogFailure("NpgsqlException").Should().BeFalse();

        logger.Lines.Should().HaveCount(1);
        var line = logger.Lines.Single();
        line.Level.Should().Be(LogLevel.Warning);
        line.Text.Should().Contain("NpgsqlException").And.Contain("1 time(s)");
        line.Exception.Should().BeNull();
    }

    private static IConfiguration Config(string? backlogIntervalSeconds) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkflowRunner.BacklogIntervalKey] = backlogIntervalSeconds,
            })
            .Build();

    private sealed class CapturingLogger : ILogger<WorkflowRunner>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Text, Exception? Exception)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue((logLevel, formatter(state, exception), exception));
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
