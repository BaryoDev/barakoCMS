using System.Net;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Cancelling a run: what it moves, what it leaves, what the runner does with a cancelled run, and
/// what happens when a cancel and a claim meet.
/// </summary>
/// <remarks>
/// The races are taken explicitly, the way WorkflowRunTests takes the claim: one side loads, the
/// other writes, and the first side's save is what is asserted on. The one moment that cannot be
/// reached from outside, a cancel landing while an action is out, is reached from inside the action
/// with <see cref="HookedRunnerAction"/>.
/// </remarks>
[Collection("Sequential")]
public class WorkflowRunCancelTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunCancelTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Theory]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Cancelled, RunStatus.Cancelled)]
    [InlineData(AttemptStatus.Failed, AttemptStatus.Cancelled, RunStatus.Cancelled)]
    [InlineData(AttemptStatus.Cancelled, AttemptStatus.Cancelled, RunStatus.Cancelled)]
    [InlineData(AttemptStatus.Running, AttemptStatus.Cancelled, RunStatus.Running)]
    [InlineData(AttemptStatus.Succeeded, AttemptStatus.Failed, RunStatus.PartiallyFailed)]
    public void A_run_with_a_cancelled_action_reads_cancelled_once_nothing_is_in_flight(
        AttemptStatus first, AttemptStatus second, RunStatus expected)
    {
        var run = new WorkflowRun
        {
            Actions =
            [
                new WorkflowActionAttempt { Ordinal = 0, Status = first, LeaseExpiresAt = WorkflowStopHarness.ParkedUntil },
                new WorkflowActionAttempt { Ordinal = 1, Status = second },
            ],
        };

        run.Recompute();

        run.Status.Should().Be(expected);
    }

    [Fact]
    public void Cancel_moves_what_has_not_started_and_leaves_an_action_under_a_live_lease()
    {
        var now = DateTimeOffset.UtcNow;
        var run = new WorkflowRun
        {
            Actions =
            [
                new WorkflowActionAttempt { Ordinal = 0, Status = AttemptStatus.Succeeded },
                new WorkflowActionAttempt { Ordinal = 1, Status = AttemptStatus.Running, LeasedBy = "live", LeaseExpiresAt = now.AddMinutes(5) },
                new WorkflowActionAttempt { Ordinal = 2, Status = AttemptStatus.Running, LeasedBy = "dead", LeaseExpiresAt = now.AddMinutes(-5) },
                new WorkflowActionAttempt { Ordinal = 3, Status = AttemptStatus.Pending, NextAttemptAt = now.AddMinutes(10) },
            ],
        };

        run.Cancel(now).Should().Be(2, "the abandoned attempt and the waiting one");

        run.Actions.Should().HaveCount(4);
        run.Actions.Select(a => a.Status).Should().Equal(
            AttemptStatus.Succeeded, AttemptStatus.Running, AttemptStatus.Cancelled, AttemptStatus.Cancelled);
        run.Actions[1].LeasedBy.Should().Be("live");
        run.Actions[3].NextAttemptAt.Should().BeNull();
        run.CancelledAt.Should().Be(now);
        run.Status.Should().Be(RunStatus.Running);
        run.NextDueAt.Should().Be(now.AddMinutes(5), "the runner looks again when the live lease ends, and at nothing before");
    }

    [Fact]
    public async Task Cancelling_a_run_moves_its_waiting_actions_and_keeps_what_already_ran()
    {
        var admin = await _harness.AdminAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(),
        [
            WorkflowStopHarness.Finished(AttemptStatus.Succeeded),
            WorkflowStopHarness.Waiting(),
            WorkflowStopHarness.Waiting(),
        ]);

        var res = await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);

        using (var doc = JsonDocument.Parse(body))
        {
            var status = doc.RootElement.GetProperty("status");
            status.ValueKind.Should().Be(JsonValueKind.String, "a run's status crosses the wire as a name");
            status.GetString().Should().Be("Cancelled");
            doc.RootElement.GetProperty("cancelledAt").ValueKind.Should().Be(JsonValueKind.String);

            var actions = doc.RootElement.GetProperty("actions");
            actions.GetArrayLength().Should().Be(3);
            actions.EnumerateArray().Select(a => a.GetProperty("status").GetString())
                .Should().Equal("Succeeded", "Cancelled", "Cancelled");
        }

        var stored = await _harness.LoadRunAsync(run.Id);
        stored.Status.Should().Be(RunStatus.Cancelled);
        stored.NextDueAt.Should().BeNull("a cancelled run must not come back in the due query");
        stored.CompletedAt.Should().NotBeNull();

        var audit = await _harness.AuditOfAsync("workflow.run.cancelled", run.Id);
        audit.Should().HaveCount(1);
        audit[0].ActorUsername.Should().NotBeNullOrEmpty();
        Convert.ToInt32(audit[0].Metadata!["cancelledActions"]).Should().Be(2);
    }

    /// <summary>
    /// Through the runner: the cancelled run's action is never called, and the run beside it is.
    /// </summary>
    [Fact]
    public async Task A_cancelled_runs_waiting_action_never_executes()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();

        var cancelled = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [WorkflowStopHarness.Waiting()]);
        var control = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [WorkflowStopHarness.Waiting()]);

        (await admin.PostAsync($"/api/workflow-runs/{cancelled.Id}/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await _harness.MakeDueAsync(control.Id);
            await _harness.DrainAsync();
        });

        WorkflowStopHarness.TimesRun(cancelled.Actions[0]).Should().Be(0);
        WorkflowStopHarness.TimesRun(control.Actions[0]).Should().Be(1, "the runner did run, and ran the run nobody cancelled");

        var after = await _harness.LoadRunAsync(cancelled.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Attempts.Should().Be(0);
    }

    /// <summary>
    /// A run that is marked stopped and still has an action due is not claimed.
    /// </summary>
    /// <remarks>
    /// The shape a node that does not know about cancelling could leave, and the one a retried
    /// attempt of a stopped run takes. The due query offers it, so the runner is what refuses it.
    /// </remarks>
    [Fact]
    public async Task A_run_marked_cancelled_with_an_action_still_due_is_cancelled_by_the_runner_and_not_executed()
    {
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun stopped = null!;
        WorkflowRun control = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            stopped = await _harness.SeedRunAsync(Guid.NewGuid(), contentId,
                [WorkflowStopHarness.Waiting(due: true)], cancelledAt: DateTimeOffset.UtcNow);
            control = await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [WorkflowStopHarness.Waiting(due: true)]);

            stopped.NextDueAt.Should().Be(WorkflowRun.DueAtOnce, "the query has to offer it for the runner to be what refuses it");

            await _harness.DrainAsync();
        });

        WorkflowStopHarness.TimesRun(stopped.Actions[0]).Should().Be(0);
        WorkflowStopHarness.TimesRun(control.Actions[0]).Should().Be(1);

        var after = await _harness.LoadRunAsync(stopped.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled);
        after.Status.Should().Be(RunStatus.Cancelled);
        after.NextDueAt.Should().BeNull();
    }

    /// <summary>
    /// A cancel that arrives while an action is out: the action is left to finish, nothing after it
    /// starts, and its failure is not queued again.
    /// </summary>
    /// <remarks>
    /// The action fails in the way the runner retries. Without the cancel the first action would be
    /// Pending again with a wait, and the second would run.
    /// </remarks>
    [Fact]
    public async Task A_cancel_during_a_claimed_action_leaves_it_running_and_stops_the_run_after_it()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();

        HttpStatusCode? answered = null;
        string seenDuring = string.Empty;
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            // Not due yet, so the hook is in place before anything can claim it.
            run = await _harness.SeedRunAsync(Guid.NewGuid(), contentId,
                [WorkflowStopHarness.Waiting("HookedRunner"), WorkflowStopHarness.Waiting()]);

            HookedRunnerAction.DuringRun[run.Id.ToString()] = async () =>
            {
                var res = await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, CancellationToken.None);
                answered = res.StatusCode;
                seenDuring = await res.Content.ReadAsStringAsync(CancellationToken.None);
            };

            await _harness.MakeDueAsync(run.Id);
            await _harness.DrainAsync();
        });

        HookedRunnerAction.RunsByRun.GetValueOrDefault(run.Id.ToString()).Should().Be(1, "the first action has to have been claimed and run");
        answered.Should().Be(HttpStatusCode.OK, "{0}", seenDuring);

        using (var doc = JsonDocument.Parse(seenDuring))
        {
            doc.RootElement.GetProperty("status").GetString().Should().Be("Running");
            doc.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("status").GetString())
                .Should().Equal(["Running", "Cancelled"], "the claimed action is left and the waiting one is moved");
        }

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(2);
        after.Actions[0].Attempts.Should().Be(1);
        after.Actions[0].Error.Should().Contain("503", "its outcome is still recorded");
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled, "a failure the runner would retry is not queued again on a stopped run");
        after.Actions[0].NextAttemptAt.Should().BeNull();
        after.Actions[1].Status.Should().Be(AttemptStatus.Cancelled);
        after.Status.Should().Be(RunStatus.Cancelled);
        after.NextDueAt.Should().BeNull();

        WorkflowStopHarness.TimesRun(run.Actions[1]).Should().Be(0, "nothing after the running action is started");
    }

    /// <summary>
    /// A claim that loaded the run before the cancel was saved is refused, and the action does not run.
    /// </summary>
    [Fact]
    public async Task A_claim_that_read_the_run_before_a_cancel_is_refused()
    {
        var admin = await _harness.AdminAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(), [WorkflowStopHarness.Waiting()]);

        await using var claim = _harness.Store.LightweightSession();
        var read = await claim.LoadAsync<WorkflowRun>(run.Id, Ct);

        (await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        read!.Actions[0].Status = AttemptStatus.Running;
        read.Actions[0].LeasedBy = "node-a";
        read.Actions[0].LeaseExpiresAt = WorkflowStopHarness.ParkedUntil;
        read.Recompute();
        claim.Update(read);

        var claiming = async () => await claim.SaveChangesAsync(Ct);

        (await claiming.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Contain("Concurrency",
                "the claim read the run before it was cancelled, and both cannot be saved");

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled, "the cancel stands");
        after.Actions[0].LeasedBy.Should().BeNull();
    }

    /// <summary>
    /// The other order: a cancel that read the run before a claim was saved is refused.
    /// </summary>
    /// <remarks>
    /// Taken on the document, where the endpoint makes its write. The endpoint turning that refusal
    /// into a 409 is not driven here, because nothing outside the request can get between its read
    /// and its save.
    /// </remarks>
    [Fact]
    public async Task A_cancel_that_read_the_run_before_a_claim_is_refused()
    {
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(), [WorkflowStopHarness.Waiting()]);

        await using var cancel = _harness.Store.LightweightSession();
        await using var claim = _harness.Store.LightweightSession();

        var forCancel = await cancel.LoadAsync<WorkflowRun>(run.Id, Ct);
        var forClaim = await claim.LoadAsync<WorkflowRun>(run.Id, Ct);

        forClaim!.Actions[0].Status = AttemptStatus.Running;
        forClaim.Actions[0].LeasedBy = "node-a";
        forClaim.Actions[0].LeaseExpiresAt = WorkflowStopHarness.ParkedUntil;
        forClaim.Recompute();
        claim.Update(forClaim);
        await claim.SaveChangesAsync(Ct);

        forCancel!.Cancel(DateTimeOffset.UtcNow).Should().Be(1, "as this side read it, the action was still waiting");
        cancel.Update(forCancel);

        var cancelling = async () => await cancel.SaveChangesAsync(Ct);

        (await cancelling.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Contain("Concurrency",
                "a cancel written over a claim would mark an action cancelled that is out with a third party");

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Running, "the claim stands");
        after.Actions[0].LeasedBy.Should().Be("node-a");
        after.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task A_running_action_under_a_live_lease_is_left_by_a_cancel()
    {
        var admin = await _harness.AdminAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(),
            [WorkflowStopHarness.RunningElsewhere(), WorkflowStopHarness.Waiting()]);

        var res = await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await res.Content.ReadAsStringAsync(Ct));

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(2);
        after.Actions[0].Status.Should().Be(AttemptStatus.Running);
        after.Actions[0].LeasedBy.Should().Be(WorkflowStopHarness.OtherNode);
        after.Actions[1].Status.Should().Be(AttemptStatus.Cancelled);
        after.Status.Should().Be(RunStatus.Running);
        after.CancelledAt.Should().NotBeNull();
        after.NextDueAt.Should().Be(after.Actions[0].LeaseExpiresAt);
    }

    [Fact]
    public async Task Retrying_an_action_of_a_cancelled_run_is_refused()
    {
        var admin = await _harness.AdminAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(),
            [WorkflowStopHarness.Finished(AttemptStatus.Failed), WorkflowStopHarness.Waiting()]);

        (await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var failed = await admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/0/retry", null, Ct);
        var cancelled = await admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/1/retry", null, Ct);

        failed.StatusCode.Should().Be(HttpStatusCode.Conflict, "the run was stopped, whatever state this action is in");
        (await failed.Content.ReadAsStringAsync(Ct)).Should().Contain("cancelled");
        cancelled.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(2);
        after.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Failed, AttemptStatus.Cancelled);
        after.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task Cancelling_a_finished_run_is_refused_and_an_unknown_one_is_not_found()
    {
        var admin = await _harness.AdminAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(),
            [WorkflowStopHarness.Finished(AttemptStatus.Succeeded)]);

        (await admin.PostAsync($"/api/workflow-runs/{run.Id}/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsync($"/api/workflow-runs/{Guid.NewGuid()}/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PostAsync("/api/workflow-runs/not-a-guid/cancel", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var after = await _harness.LoadRunAsync(run.Id);
        after.Status.Should().Be(RunStatus.Succeeded);
        after.CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_a_run_needs_a_token_and_the_capability()
    {
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), Guid.NewGuid(), [WorkflowStopHarness.Waiting()]);
        var path = $"/api/workflow-runs/{run.Id}/cancel";

        (await _harness.Anonymous().PostAsync(path, null, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await _harness.WithoutTheCapabilityAsync()).PostAsync(path, null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var untouched = await _harness.LoadRunAsync(run.Id);
        untouched.Status.Should().Be(RunStatus.Pending, "neither caller may have cancelled it");
        untouched.CancelledAt.Should().BeNull();

        (await (await _harness.AdminAsync()).PostAsync(path, null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the same request from a caller who holds the capability is served, so the refusals are the gate");
    }
}
