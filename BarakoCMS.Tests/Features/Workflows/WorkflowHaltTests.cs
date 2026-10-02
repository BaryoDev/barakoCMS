using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action set to halt, through the runner and through the retry endpoint: what runs after it
/// fails, what a retry of it queues again, and that a run with no policy behaves as it did.
/// </summary>
/// <remarks>
/// Every run that is due is seeded and drained with the fixture's hosted runner stopped, so the
/// only runner is the one the test drives. The failing action is a type no host has a handler for,
/// which the runner records as a permanent failure on the first attempt. The actions after it are
/// <see cref="CountingRunnerAction"/>, so "not run" is a count of zero and not only a status.
/// </remarks>
[Collection("Sequential")]
public class WorkflowHaltTests
{
    private const string NoHandler = "NoSuchAction";

    private readonly WorkflowStopHarness _harness;

    public WorkflowHaltTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task A_halting_action_that_fails_for_good_leaves_the_later_actions_skipped_and_not_run()
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [
                Halting(WorkflowStopHarness.Waiting(NoHandler, due: true)),
                WorkflowStopHarness.Waiting(due: true),
                WorkflowStopHarness.Waiting(due: true),
            ]);

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(3);
        run.Actions.Select(a => a.Status).Should().Equal(
            AttemptStatus.Failed, AttemptStatus.Skipped, AttemptStatus.Skipped);
        run.Actions[0].Retryable.Should().BeFalse("no handler is a failure a retry does not fix");

        foreach (var skipped in run.Actions.Skip(1))
        {
            skipped.HaltedBy.Should().Be(0);
            skipped.Attempts.Should().Be(0);
            skipped.Error.Should().Be(WorkflowRun.SkippedAfterHalt);
            WorkflowStopHarness.TimesRun(skipped).Should().Be(0, "a skipped action never went out");
        }

        run.Status.Should().Be(RunStatus.Failed, "nothing of the run went out");
        run.NextDueAt.Should().BeNull("the run is finished, so the due query must not offer it again");
        run.CompletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// The same run with no policy on it, stored the way a run was before the policy existed.
    /// </summary>
    /// <remarks>
    /// A guard: it passes with and without the change. The fields are removed from the stored JSON
    /// so what the runner reads is an old run and not a new one that happens to say Continue.
    /// </remarks>
    [Fact]
    public async Task A_run_stored_without_a_policy_still_runs_the_actions_after_a_failure()
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [
                WorkflowStopHarness.Waiting(NoHandler, due: true),
                WorkflowStopHarness.Waiting(due: true),
            ]);
            await StripThePolicyAsync(seeded.Id);

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Failed, AttemptStatus.Succeeded);
        run.Actions[0].OnFailure.Should().Be(WorkflowFailurePolicy.Continue);
        run.Actions[1].HaltedBy.Should().BeNull();
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(1);
        run.Status.Should().Be(RunStatus.PartiallyFailed);
    }

    /// <summary>
    /// A halting action that failed in a way the runner retries: the action after it waits with it.
    /// </summary>
    [Fact]
    public async Task A_halting_action_queued_for_another_try_holds_back_the_action_after_it()
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [
                Halting(WorkflowStopHarness.Waiting("HookedRunner", due: true)),
                WorkflowStopHarness.Waiting(due: true),
            ]);

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions[0].Status.Should().Be(AttemptStatus.Pending, "a 503 is queued for another try");
        run.Actions[0].Attempts.Should().BeGreaterThanOrEqualTo(1, "a long drain can reach its second try");
        run.Actions[0].NextAttemptAt.Should().NotBeNull();

        run.Actions[1].Status.Should().Be(AttemptStatus.Pending);
        run.Actions[1].Attempts.Should().Be(0);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);

        run.NextDueAt.Should().Be(run.Actions[0].NextAttemptAt, "the run is due when the halting action is");
    }

    /// <summary>
    /// The runner's own check, apart from the due query. The run is offered although its halting
    /// action is still waiting, which is what a run written by a node without the change looks like.
    /// </summary>
    /// <remarks>
    /// Without the stale value the due query does not offer the run at all, and the test would pass
    /// on the query alone.
    /// </remarks>
    [Fact]
    public async Task A_run_offered_while_its_halting_action_waits_is_not_claimed_past_it()
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [
                Halting(WorkflowStopHarness.Waiting(NoHandler)),
                WorkflowStopHarness.Waiting(due: true),
            ]);

            await using (var session = _harness.Store.LightweightSession())
            {
                var stale = await session.LoadAsync<WorkflowRun>(seeded.Id, Ct);
                stale.Should().NotBeNull();
                stale!.NextDueAt = WorkflowRun.DueAtOnce;
                session.Update(stale);
                await session.SaveChangesAsync(Ct);
            }

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions[0].Attempts.Should().Be(0, "it is not due");
        run.Actions[1].Status.Should().Be(AttemptStatus.Pending);
        run.Actions[1].Attempts.Should().Be(0);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);

        run.NextDueAt.Should().Be(run.Actions[0].NextAttemptAt,
            "the runner writes the real value back, so the run stops being offered on every pass");
    }

    /// <summary>
    /// The resume: retrying the action that halted the run queues the actions it skipped, and the
    /// runner then runs all of them in order.
    /// </summary>
    [Fact]
    public async Task Retrying_the_halted_action_queues_the_skipped_ones_and_the_runner_runs_them()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun seeded = null!;
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [
                Halting(WorkflowStopHarness.Finished(AttemptStatus.Failed)),
                SkippedBehind(0),
                SkippedBehind(0),
            ]);
            seeded.Status.Should().Be(RunStatus.Failed, "the run has to be finished for the retry to be a resume");

            var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/actions/0/retry", null, Ct);
            var body = await res.Content.ReadAsStringAsync(Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);

            using (var doc = JsonDocument.Parse(body))
            {
                doc.RootElement.GetProperty("status").GetString().Should().Be("Pending");

                var actions = doc.RootElement.GetProperty("actions");
                actions.GetArrayLength().Should().Be(3);
                actions.EnumerateArray().Select(a => a.GetProperty("status").GetString())
                    .Should().Equal("Pending", "Pending", "Pending");
                actions.EnumerateArray().Select(a => a.GetProperty("haltedBy").ValueKind)
                    .Should().Equal(JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null);
            }

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(3);
        foreach (var attempt in run.Actions)
        {
            attempt.Status.Should().Be(AttemptStatus.Succeeded);
            attempt.HaltedBy.Should().BeNull();
            attempt.Error.Should().BeNull();
            WorkflowStopHarness.TimesRun(attempt).Should().Be(1);
        }

        run.Status.Should().Be(RunStatus.Succeeded);

        var audit = await _harness.AuditOfAsync("workflow.action.retried", seeded.Id);
        audit.Should().HaveCount(1);
        Convert.ToInt32(audit[0].Metadata!["resumedActions"].ToString()).Should().Be(2);
    }

    /// <summary>
    /// A resume is not a pass: the retried action fails again, and the action behind it is skipped
    /// again without having run.
    /// </summary>
    [Fact]
    public async Task A_resumed_run_whose_halting_action_fails_again_skips_the_later_action_again()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var failed = Halting(WorkflowStopHarness.Finished(AttemptStatus.Failed));
            failed.ActionType = NoHandler;

            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [failed, SkippedBehind(0)]);

            var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/actions/0/retry", null, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await res.Content.ReadAsStringAsync(Ct));

            var queued = await _harness.LoadRunAsync(seeded.Id);
            queued.Actions.Should().HaveCount(2);
            queued.Actions[1].Status.Should().Be(AttemptStatus.Pending, "the retry has to have queued it for the skip below to be a second one");

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Attempts.Should().Be(2, "it was tried once more");
        run.Actions[1].Status.Should().Be(AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);
        run.Actions[1].Attempts.Should().Be(0);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);
        run.Status.Should().Be(RunStatus.Failed);
        run.NextDueAt.Should().BeNull();
    }

    /// <summary>
    /// The other way an action fails for good: a failure the runner would retry, on its last try.
    /// </summary>
    [Fact]
    public async Task A_halting_action_that_fails_on_its_last_attempt_skips_the_later_action()
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var lastTry = Halting(WorkflowStopHarness.Waiting("ThrowingRunner", due: true));
            lastTry.Attempts = WorkflowRetryPolicy.MaxAttempts - 1;

            var seeded = await _harness.SeedRunAsync(workflowId, contentId, [lastTry, WorkflowStopHarness.Waiting(due: true)]);

            await _harness.DrainAsync();

            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(2);
        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Retryable.Should().BeTrue("it ran out of tries, it was not refused");
        run.Actions[0].Attempts.Should().Be(WorkflowRetryPolicy.MaxAttempts);
        run.Actions[1].Status.Should().Be(AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);
        run.Status.Should().Be(RunStatus.Failed);
    }

    /// <summary>
    /// A skipped action cannot be retried on its own. That would run it past the failure.
    /// </summary>
    [Fact]
    public async Task Retrying_an_action_skipped_behind_a_failure_is_refused_and_changes_nothing()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var seeded = await _harness.SeedRunAsync(workflowId, await _harness.StoreContentAsync(),
        [
            Halting(WorkflowStopHarness.Finished(AttemptStatus.Failed)),
            SkippedBehind(0),
        ]);

        var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/actions/1/retry", null, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "{0}", body);
        body.Should().Contain("action 0");

        var run = await _harness.LoadRunAsync(seeded.Id);
        run.Actions.Should().HaveCount(2);
        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Failed, AttemptStatus.Skipped);
        run.Actions[1].HaltedBy.Should().Be(0);
        run.Status.Should().Be(RunStatus.Failed);
        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0);

        (await _harness.AuditOfAsync("workflow.action.retried", seeded.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// A guard, passing with and without the change: an action skipped because the content went
    /// carries no HaltedBy, and can be retried as before.
    /// </summary>
    [Fact]
    public async Task Retrying_an_action_skipped_because_the_content_went_is_still_accepted()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var seeded = await _harness.SeedRunAsync(workflowId, Guid.NewGuid(),
        [
            WorkflowStopHarness.Finished(AttemptStatus.Skipped),
        ]);

        var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/actions/0/retry", null, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_halted_run_says_over_the_api_which_action_halted_it()
    {
        var admin = await _harness.AdminAsync();
        var seeded = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(),
        [
            WorkflowStopHarness.Finished(AttemptStatus.Succeeded),
            Halting(WorkflowStopHarness.Finished(AttemptStatus.Failed)),
            SkippedBehind(1),
        ]);

        var res = await admin.GetAsync($"/api/workflow-runs/{seeded.Id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetString().Should().Be("PartiallyFailed");

        var actions = doc.RootElement.GetProperty("actions");
        actions.GetArrayLength().Should().Be(3);
        actions.EnumerateArray().Select(a => a.GetProperty("onFailure").GetString())
            .Should().Equal("Continue", "Halt", "Continue");

        actions[0].GetProperty("haltedBy").ValueKind.Should().Be(JsonValueKind.Null);
        actions[2].GetProperty("status").GetString().Should().Be("Skipped");
        actions[2].GetProperty("haltedBy").GetInt32().Should().Be(1);
        actions[2].GetProperty("error").GetString().Should().Be(WorkflowRun.SkippedAfterHalt);
    }

    private static WorkflowActionAttempt Halting(WorkflowActionAttempt attempt)
    {
        attempt.OnFailure = WorkflowFailurePolicy.Halt;
        return attempt;
    }

    private static WorkflowActionAttempt SkippedBehind(int ordinal) => new()
    {
        ActionType = WorkflowStopHarness.Counting,
        Status = AttemptStatus.Skipped,
        HaltedBy = ordinal,
        Error = WorkflowRun.SkippedAfterHalt,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Takes a stored run's attempts back to the shape they had before the policy was kept.</summary>
    private async Task StripThePolicyAsync(Guid runId)
    {
        await using var conn = _harness.Store.Storage.Database.CreateConnection();
        await conn.OpenAsync(Ct);

        await using (var strip = conn.CreateCommand())
        {
            strip.CommandText =
                "update public.mt_doc_workflow_runs set data = data "
              + "#- '{Actions,0,OnFailure}' #- '{Actions,0,HaltedBy}' "
              + "#- '{Actions,1,OnFailure}' #- '{Actions,1,HaltedBy}' "
              + $"where id = '{runId}'";
            (await strip.ExecuteNonQueryAsync(Ct)).Should().Be(1);
        }

        await using var check = conn.CreateCommand();
        check.CommandText = $"select data::text from public.mt_doc_workflow_runs where id = '{runId}'";
        var stored = (string?)await check.ExecuteScalarAsync(Ct);

        stored.Should().NotBeNull();
        stored.Should().Contain("ActionType", "the attempts are still there");
        stored.Should().NotContain("OnFailure", "the stored run has to be without the field");
        stored.Should().NotContain("HaltedBy");
    }
}
