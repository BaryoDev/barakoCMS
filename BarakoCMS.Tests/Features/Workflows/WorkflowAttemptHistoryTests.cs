using System.Net;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The history an action keeps of every try: what is in it, what it never holds, how it is capped,
/// and that two writers on one run do not lose each other's entries.
/// </summary>
[Collection("Integration1")]
public class WorkflowAttemptHistoryTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowAttemptHistoryTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static WorkflowActionAttempt Action(int failTimes, string? error = null)
    {
        var attempt = WorkflowStopHarness.Waiting(AttemptHistoryRunnerAction.ActionType);
        attempt.Parameters["FailTimes"] = failTimes.ToString();
        if (error is not null) attempt.Parameters["Error"] = error;
        return attempt;
    }

    [Fact]
    public async Task An_error_loses_a_guid_shaped_value_and_the_token_beside_it()
    {
        var contentId = await _harness.StoreContentAsync();
        var missing = Guid.NewGuid();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId,
            [
                Action(failTimes: 5, error: $"Content {missing} not found, sent Bearer abcDEF123456xyz"),
            ]);

            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.History.Should().HaveCount(1);
        foreach (var text in new[] { attempt.Error, attempt.History[0].Error })
        {
            text.Should().NotContain(missing.ToString());
            text.Should().Contain("not found");
            text.Should().NotContain("abcDEF123456xyz");
        }
    }

    [Fact]
    public async Task A_run_stored_before_history_counts_its_earlier_attempts_as_dropped_when_it_gets_a_new_one()
    {
        var contentId = await _harness.StoreContentAsync();
        var legacy = Action(failTimes: 9);
        legacy.Attempts = 3;
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [legacy]);
            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.Attempts.Should().Be(4);
        attempt.History.Should().HaveCount(1);
        attempt.History[0].Number.Should().Be(4);
        attempt.DroppedAttempts.Should().Be(3);
    }

    [Fact]
    public async Task An_action_that_fails_twice_then_succeeds_keeps_three_entries_in_order()
    {
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [Action(failTimes: 2)]);

            for (var i = 0; i < 3; i++)
            {
                await _harness.MakeDueAsync(run.Id);
                await _harness.DrainAsync();
            }
        });

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.Status.Should().Be(AttemptStatus.Succeeded);
        attempt.Error.Should().BeNull();
        attempt.History.Should().HaveCount(3);
        attempt.History.Select(e => e.Number).Should().Equal(1, 2, 3);
        attempt.History.Select(e => e.Outcome).Should().Equal(AttemptStatus.Failed, AttemptStatus.Failed, AttemptStatus.Succeeded);
        attempt.History[0].Error.Should().Contain("503");
        attempt.History[0].ResponseStatus.Should().Be(503);
        attempt.History[0].Retryable.Should().BeTrue();

        var success = attempt.History[2];
        success.Error.Should().BeNull("a success clears the error, here as on the attempt");
        success.ResponseStatus.Should().Be(200);
        success.DurationMs.Should().NotBeNull();
        success.StartedAt.Should().NotBeNull();
        success.FinishedAt.Should().BeOnOrAfter(success.StartedAt!.Value);
        attempt.DroppedAttempts.Should().Be(0);
    }

    [Fact]
    public async Task A_credential_in_an_error_is_held_by_neither_the_attempt_nor_its_history_entry()
    {
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId,
            [
                Action(failTimes: 5, error: "denied for Bearer abcDEF123456xyz at https://x.test/cb?token=s3cretValue99&a=1"),
            ]);

            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.History.Should().HaveCount(1);
        foreach (var text in new[] { attempt.Error, attempt.History[0].Error })
        {
            text.Should().NotBeNullOrEmpty();
            text.Should().NotContain("abcDEF123456xyz");
            text.Should().NotContain("s3cretValue99");
            text.Should().Contain("denied for");
        }
    }

    [Fact]
    public void The_history_keeps_the_newest_twenty_and_counts_what_it_dropped()
    {
        var attempt = new WorkflowActionAttempt();

        for (var n = 1; n <= 25; n++) attempt.RecordAttempt(new WorkflowAttemptEntry { Number = n });

        attempt.History.Should().HaveCount(WorkflowActionAttempt.HistoryCap);
        attempt.History[0].Number.Should().Be(6, "the oldest five are the ones dropped");
        attempt.History[^1].Number.Should().Be(25);
        attempt.DroppedAttempts.Should().Be(5);

        attempt.RecordAttempt(new WorkflowAttemptEntry { Number = 26 });

        attempt.History.Should().HaveCount(WorkflowActionAttempt.HistoryCap);
        attempt.History[0].Number.Should().Be(7);
        attempt.DroppedAttempts.Should().Be(6, "the count survives a second drop");
    }

    [Fact]
    public async Task A_manual_retry_keeps_the_history_and_the_dropped_count()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var failed = Action(failTimes: 100);
            failed.Status = AttemptStatus.Failed;
            failed.NextAttemptAt = null;
            failed.Retryable = false;
            failed.Attempts = 25;
            for (var n = 6; n <= 25; n++)
            {
                failed.History.Add(new WorkflowAttemptEntry { Number = n, Outcome = AttemptStatus.Failed, Error = "old" });
            }

            failed.DroppedAttempts = 5;
            run = await _harness.SeedRunAsync(workflowId, contentId, [failed]);

            var retry = await admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/0/retry", null, Ct);
            retry.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await retry.Content.ReadAsStringAsync(Ct));

            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.History.Should().HaveCount(WorkflowActionAttempt.HistoryCap);
        attempt.DroppedAttempts.Should().Be(6, "the retry added a try and pushed the oldest out");
        attempt.History.Select(e => e.Number).Should().NotContain(6);
        attempt.History[^1].Error.Should().Contain("503");
    }

    [Fact]
    public async Task Two_writers_on_one_run_both_keep_their_history_entry_after_the_loser_reloads()
    {
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [Action(failTimes: 100)]);

            RunnerSaveRaceListener.BeforeSave[run.Id] = async () =>
            {
                await using var other = _harness.Store.LightweightSession();
                var latest = await other.LoadAsync<WorkflowRun>(run.Id, CancellationToken.None);
                latest!.Actions[0].RecordAttempt(new WorkflowAttemptEntry
                {
                    Number = 99,
                    Outcome = AttemptStatus.Failed,
                    Error = "written by the other writer",
                });
                other.Update(latest);
                await other.SaveChangesAsync(CancellationToken.None);
            };

            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        RunnerSaveRaceListener.BeforeSave.ContainsKey(run.Id).Should().BeFalse("the race was taken, so the runner's first save was refused");

        var attempt = (await _harness.LoadRunAsync(run.Id)).Actions.Single();

        attempt.History.Should().HaveCount(2);
        attempt.History.Select(e => e.Number).Should().BeEquivalentTo(new[] { 99, 1 });
        attempt.History.Single(e => e.Number == 99).Error.Should().Be("written by the other writer");
        attempt.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_run_stored_before_history_shows_one_entry_and_the_read_does_not_rewrite_it()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();
        var failed = WorkflowStopHarness.Finished(AttemptStatus.Failed);
        failed.Error = "the provider answered 503";
        failed.ResponseStatus = 503;
        failed.DurationMs = 42;
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [failed]);

        await using (var session = _harness.Store.LightweightSession())
        {
            session.QueueSqlCommand(
                "update public.mt_doc_workflow_runs set data = data - 'UpdatedAt' where id = ?", run.Id);
            await session.SaveChangesAsync(Ct);
        }

        var before = await _harness.LoadRunAsync(run.Id);
        before.UpdatedAt.Should().Be(default(DateTimeOffset), "the stamp was removed to look like an older run");
        before.Actions[0].History.Should().BeEmpty();

        var res = await admin.GetAsync($"/api/workflow-runs/{run.Id}", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));

        var action = doc.RootElement.GetProperty("actions")[0];
        var history = action.GetProperty("history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("outcome").GetString().Should().Be("Failed");
        history[0].GetProperty("responseStatus").GetInt32().Should().Be(503);
        history[0].GetProperty("durationMs").GetInt64().Should().Be(42);
        action.GetProperty("droppedAttempts").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("updatedAt").ValueKind.Should().Be(JsonValueKind.Null);

        var after = await _harness.LoadRunAsync(run.Id);
        after.UpdatedAt.Should().Be(default(DateTimeOffset), "reading a run does not write it");
        after.Actions[0].History.Should().BeEmpty();
    }
}
