using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What the retention sweep removes, and the far more important question of what it does not.
/// </summary>
/// <remarks>
/// Every assertion here is paired with one that has to survive. A sweep that deleted everything
/// would satisfy "the old successful run is gone" perfectly, and this is a class whose bugs are
/// silent: nobody notices a run that was deleted too early, they notice months later that the thing
/// they wanted to look at is not there.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunRetentionTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowRunRetentionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The defaults, written out so a change to them fails a test rather than a deployment.</summary>
    private static readonly RetentionWindows Default = new(7, 90);

    private static WorkflowRun Run(RunStatus status, DateTimeOffset? completedAt, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = name,
            ContentId = Guid.NewGuid(),
            ContentType = "article",
            TriggerEvent = "Published",
            Status = status,
            CreatedAt = completedAt ?? Now.AddDays(-400),
            CompletedAt = completedAt,
        };

    private async Task<List<string>> SweepAndListAsync(
        IEnumerable<WorkflowRun> runs, RetentionWindows windows)
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var seeded = runs.Select(r => { r.WorkflowName = marker + ":" + r.WorkflowName; return r; }).ToList();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        foreach (var run in seeded) session.Store(run);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        await WorkflowRunRetentionService.SweepTenantAsync(
            session, Now, windows, null, TestContext.Current.CancellationToken);

        var survivors = await session.Query<WorkflowRun>()
            .Where(r => r.WorkflowName.StartsWith(marker))
            .ToListAsync(TestContext.Current.CancellationToken);

        return survivors.Select(r => r.WorkflowName[(marker.Length + 1)..]).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task A_successful_run_past_its_window_goes_and_one_inside_it_stays()
    {
        var survivors = await SweepAndListAsync(
        [
            Run(RunStatus.Succeeded, Now.AddDays(-8), "old-success"),
            Run(RunStatus.Succeeded, Now.AddDays(-6), "recent-success"),
        ], Default);

        survivors.Should().Equal(["recent-success"],
            "seven days is the window, so eight days old goes and six stays");
    }

    [Fact]
    public async Task A_failure_outlives_a_success_of_the_same_age()
    {
        // The whole point of the issue in one test. Both are thirty days old, which is past the
        // success window and well inside the failure one.
        var survivors = await SweepAndListAsync(
        [
            Run(RunStatus.Succeeded, Now.AddDays(-30), "success"),
            Run(RunStatus.Failed, Now.AddDays(-30), "failure"),
            Run(RunStatus.PartiallyFailed, Now.AddDays(-30), "partial"),
        ], Default);

        survivors.Should().Equal(["failure", "partial"],
            "a failure is interesting until somebody deals with it, and a partial failure holds one");
    }

    [Fact]
    public async Task A_failure_past_its_own_window_does_go()
    {
        // Paired with the test above, which would pass just as well against a sweep that never
        // deletes a failure at all.
        var survivors = await SweepAndListAsync(
        [
            Run(RunStatus.Failed, Now.AddDays(-91), "ancient-failure"),
            Run(RunStatus.Failed, Now.AddDays(-89), "old-failure"),
        ], Default);

        survivors.Should().Equal(["old-failure"]);
    }

    [Fact]
    public async Task Nothing_unfinished_is_ever_removed_however_old_it_is()
    {
        // The hard rule. These are aged past both windows by years, because a run whose provider has
        // been unreachable for a fortnight is still an email somebody is waiting for, and the window
        // must not be what decides that.
        var survivors = await SweepAndListAsync(
        [
            Run(RunStatus.Pending, null, "pending"),
            Run(RunStatus.Running, null, "running"),
            Run(RunStatus.Succeeded, Now.AddDays(-400), "ancient-success"),
        ], Default);

        survivors.Should().Equal(["pending", "running"],
            "unfinished work is not old, it is unfinished, and the success beside them proves the "
          + "sweep ran at all");
    }

    [Fact]
    public async Task A_window_of_zero_keeps_everything_in_that_class()
    {
        // "0 days" reads as "delete immediately" as easily as it reads as "keep forever", which is
        // exactly why it cannot be left to a default. Keeping is the direction a mistake is
        // recoverable from, and the failure window beside it shows the sweep still ran.
        var survivors = await SweepAndListAsync(
        [
            Run(RunStatus.Succeeded, Now.AddDays(-400), "kept-success"),
            Run(RunStatus.Failed, Now.AddDays(-400), "swept-failure"),
        ], new RetentionWindows(SucceededDays: 0, FailedDays: 90));

        survivors.Should().Equal(["kept-success"]);
    }

    [Fact]
    public async Task A_finished_run_with_no_completion_time_is_aged_on_when_it_was_created()
    {
        // Anomalous data rather than a normal path: a terminal run should always carry a completion
        // time. Aging it on CreatedAt instead of skipping it is what stops such a row being immortal,
        // which is the failure a retention sweep exists to prevent.
        var stale = Run(RunStatus.Succeeded, null, "no-completion");
        stale.CreatedAt = Now.AddDays(-30);

        var fresh = Run(RunStatus.Succeeded, null, "recent-no-completion");
        fresh.CreatedAt = Now.AddDays(-2);

        var survivors = await SweepAndListAsync([stale, fresh], Default);

        survivors.Should().Equal(["recent-no-completion"]);
    }
}
