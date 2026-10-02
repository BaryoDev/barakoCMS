using System.Net;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Deleting a workflow: its queued runs are cancelled in the same transaction, an action already
/// with a third party is left to finish, and the delete is recorded.
/// </summary>
/// <remarks>
/// Each test keeps a second workflow with a run of its own beside the one deleted, so "the run was
/// cancelled" cannot be satisfied by a delete that cancels every run in the tenant.
/// </remarks>
[Collection("Sequential")]
public class WorkflowDeleteTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowDeleteTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    /// <summary>
    /// How many queued runs one delete cancels, written out so a change to the limit fails here and
    /// in docs/workflow-runs.md, which quotes it, and not in a deployment.
    /// </summary>
    private const int OneDeleteCancels = 200;

    [Fact]
    public async Task Deleting_a_workflow_cancels_its_queued_runs_and_records_who_did_it()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();
        var doomed = await _harness.StoreWorkflowAsync();
        var kept = await _harness.StoreWorkflowAsync();

        var waiting = await _harness.SeedRunAsync(doomed, contentId, [WorkflowStopHarness.Waiting()]);
        var inFlight = await _harness.SeedRunAsync(doomed, contentId,
            [WorkflowStopHarness.RunningElsewhere(), WorkflowStopHarness.Waiting()]);
        var finished = await _harness.SeedRunAsync(doomed, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Succeeded)]);
        var other = await _harness.SeedRunAsync(kept, contentId, [WorkflowStopHarness.Waiting()]);

        var res = await admin.DeleteAsync($"/api/workflows/{doomed}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "{0}", await res.Content.ReadAsStringAsync(Ct));
        (await _harness.LoadWorkflowAsync(doomed)).Should().BeNull();
        (await _harness.LoadWorkflowAsync(kept)).Should().NotBeNull();

        var cancelled = await _harness.LoadRunAsync(waiting.Id);
        cancelled.Actions.Should().HaveCount(1);
        cancelled.Actions[0].Status.Should().Be(AttemptStatus.Cancelled);
        cancelled.Status.Should().Be(RunStatus.Cancelled);
        cancelled.NextDueAt.Should().BeNull();

        var left = await _harness.LoadRunAsync(inFlight.Id);
        left.Actions.Should().HaveCount(2);
        left.Actions[0].Status.Should().Be(AttemptStatus.Running, "its request is already with the third party");
        left.Actions[0].LeasedBy.Should().Be(WorkflowStopHarness.OtherNode);
        left.Actions[1].Status.Should().Be(AttemptStatus.Cancelled, "nothing after the running action is started");
        left.CancelledAt.Should().NotBeNull();
        left.Status.Should().Be(RunStatus.Running, "it ends when the running action records its outcome");

        var done = await _harness.LoadRunAsync(finished.Id);
        done.Status.Should().Be(RunStatus.Succeeded, "a finished run is history and is not rewritten");
        done.CancelledAt.Should().BeNull();

        var untouched = await _harness.LoadRunAsync(other.Id);
        untouched.Actions.Should().HaveCount(1);
        untouched.Actions[0].Status.Should().Be(AttemptStatus.Pending, "it belongs to the workflow that was kept");
        untouched.CancelledAt.Should().BeNull();

        var audit = await _harness.AuditOfAsync("workflow.deleted", doomed);
        audit.Should().HaveCount(1);
        audit[0].ActorUsername.Should().NotBeNullOrEmpty();
        audit[0].TargetType.Should().Be(nameof(WorkflowDefinition));
        audit[0].Metadata.Should().NotBeNull();
        Convert.ToInt32(audit[0].Metadata!["cancelledRuns"]).Should().Be(2, "the waiting run and the one in flight");
    }

    /// <summary>
    /// Through the runner: a run that was due when its workflow was deleted is never executed.
    /// </summary>
    /// <remarks>
    /// Both runs are due before the delete, with the hosted runner stopped. The runner does not stop
    /// a run whose definition is gone, so a delete that cancelled nothing would leave the first run
    /// to be claimed by the drain, and its action would be counted.
    /// </remarks>
    [Fact]
    public async Task A_run_that_was_due_when_its_workflow_was_deleted_never_executes()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();
        var doomed = await _harness.StoreWorkflowAsync();
        var kept = await _harness.StoreWorkflowAsync();
        WorkflowRun stopped = null!;
        WorkflowRun control = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            stopped = await _harness.SeedRunAsync(doomed, contentId, [WorkflowStopHarness.Waiting(due: true)]);
            control = await _harness.SeedRunAsync(kept, contentId, [WorkflowStopHarness.Waiting(due: true)]);

            (await admin.DeleteAsync($"/api/workflows/{doomed}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

            await _harness.DrainAsync();
        });

        var after = await _harness.LoadRunAsync(stopped.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled);
        after.Actions[0].Attempts.Should().Be(0);
        after.Status.Should().Be(RunStatus.Cancelled);
        WorkflowStopHarness.TimesRun(stopped.Actions[0]).Should().Be(0, "a cancelled run's action never executes");

        var ran = await _harness.LoadRunAsync(control.Id);
        ran.Actions.Should().HaveCount(1);
        ran.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, "the runner did run, and ran the kept workflow's run");
        WorkflowStopHarness.TimesRun(control.Actions[0]).Should().Be(1);
    }

    /// <summary>
    /// A failed run outlives its workflow as history, and its actions cannot be sent again.
    /// </summary>
    /// <remarks>
    /// A finished run is not cancelled by the delete, so without this a retry would queue an
    /// attempt with nothing to stop it: the runner does not stop a run whose definition is gone.
    /// The second run, of a workflow that still exists, is retried the same way and accepted, so the
    /// refusal is about the delete.
    /// </remarks>
    [Fact]
    public async Task Retrying_an_action_of_a_deleted_workflows_run_is_refused_and_writes_nothing()
    {
        var admin = await _harness.AdminAsync();
        var doomed = await _harness.StoreWorkflowAsync();
        var kept = await _harness.StoreWorkflowAsync();

        var orphan = await _harness.SeedRunAsync(doomed, Guid.NewGuid(), [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);
        var control = await _harness.SeedRunAsync(kept, Guid.NewGuid(), [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        (await admin.DeleteAsync($"/api/workflows/{doomed}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refused = await admin.PostAsync($"/api/workflow-runs/{orphan.Id}/actions/0/retry", null, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("deleted");

        var after = await _harness.LoadRunAsync(orphan.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        after.Status.Should().Be(RunStatus.Failed, "a refused retry leaves the run as it was");
        (await _harness.AuditOfAsync("workflow.action.retried", orphan.Id)).Should().BeEmpty();

        try
        {
            var accepted = await admin.PostAsync($"/api/workflow-runs/{control.Id}/actions/0/retry", null, Ct);

            accepted.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await accepted.Content.ReadAsStringAsync(Ct));
            (await _harness.AuditOfAsync("workflow.action.retried", control.Id)).Should().HaveCount(1);
        }
        finally
        {
            // The accepted retry is due at once. Taken out so no runner picks it up after the test.
            await using var cleanup = _harness.Store.LightweightSession();
            cleanup.DeleteWhere<WorkflowRun>(r => r.Id == control.Id);
            await cleanup.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A retry that arrives while a delete of the workflow is in progress waits for it, and is then
    /// refused, so no attempt is queued for a workflow that is gone.
    /// </summary>
    /// <remarks>
    /// The delete is this test's own session, holding the lock the delete endpoint takes, with the
    /// workflow deleted and not yet saved. A retry that did not take the lock would read the
    /// workflow as still there, answer 200 at once and leave a Pending attempt behind.
    ///
    /// The pause only gives a retry that does not wait the time to finish. What is asserted after
    /// it does not depend on timing.
    /// </remarks>
    [Fact]
    public async Task A_retry_waits_for_a_delete_in_progress_and_is_then_refused()
    {
        var admin = await _harness.AdminAsync();
        var id = await _harness.StoreWorkflowAsync();
        var run = await _harness.SeedRunAsync(id, Guid.NewGuid(), [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        Task<HttpResponseMessage> retry;

        await using (var deleting = _harness.Store.LightweightSession())
        {
            await WorkflowDefinitionLock.TakeAsync(deleting, id, Ct);
            deleting.Delete<WorkflowDefinition>(id);

            retry = admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/0/retry", null, Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);

            retry.IsCompleted.Should().BeFalse("the retry has to wait for the delete that holds the workflow");

            await deleting.SaveChangesAsync(Ct);
        }

        var res = await retry;

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("deleted");

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Failed, "no attempt is left Pending for a workflow that is gone");
        after.Status.Should().Be(RunStatus.Failed);
        (await _harness.LoadWorkflowAsync(id)).Should().BeNull();
    }

    /// <summary>
    /// The other order: a delete that arrives while a retry is in progress waits for it, then sees
    /// the attempt the retry queued and cancels it with the rest.
    /// </summary>
    /// <remarks>
    /// The retry is this test's own session, holding the lock the retry endpoint takes, with the
    /// attempt set back to Pending and not yet saved. A delete that did not take the lock would not
    /// see that attempt, would answer at once, and the attempt would be saved after it for a
    /// workflow that no longer exists.
    /// </remarks>
    [Fact]
    public async Task A_delete_waits_for_a_retry_in_progress_and_cancels_the_attempt_it_queued()
    {
        var admin = await _harness.AdminAsync();
        var id = await _harness.StoreWorkflowAsync();
        var run = await _harness.SeedRunAsync(id, Guid.NewGuid(), [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        Task<HttpResponseMessage> delete;

        await using (var retrying = _harness.Store.LightweightSession())
        {
            await WorkflowDefinitionLock.TakeAsync(retrying, id, Ct);

            var read = await retrying.LoadAsync<WorkflowRun>(run.Id, Ct);
            read!.Actions[0].Status = AttemptStatus.Pending;
            // Parked, so no runner claims it between this save and the delete's read.
            read.Actions[0].NextAttemptAt = WorkflowStopHarness.ParkedUntil;
            read.CompletedAt = null;
            read.Recompute();
            retrying.Update(read);

            delete = admin.DeleteAsync($"/api/workflows/{id}", Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(750), Ct);

            delete.IsCompleted.Should().BeFalse("the delete has to wait for the retry that holds the workflow");

            await retrying.SaveChangesAsync(Ct);
        }

        var res = await delete;

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "{0}", await res.Content.ReadAsStringAsync(Ct));
        (await _harness.LoadWorkflowAsync(id)).Should().BeNull();

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled, "no attempt is left Pending for a workflow that is gone");
        after.Status.Should().Be(RunStatus.Cancelled);
        after.NextDueAt.Should().BeNull();
    }

    [Fact]
    public void The_lock_a_delete_and_a_retry_share_names_the_tenant_and_the_workflow()
    {
        var id = Guid.NewGuid();

        WorkflowDefinitionLock.Key("acme", id).Should().NotBe(WorkflowDefinitionLock.Key("globex", id),
            "the same id in two tenants is two workflows");
        WorkflowDefinitionLock.Key("acme", id).Should().NotBe(WorkflowDefinitionLock.Key("acme", Guid.NewGuid()));
        WorkflowDefinitionLock.Key("acme", id).Should().Be(WorkflowDefinitionLock.Key("acme", id));
    }

    /// <summary>
    /// One more queued run than a delete cancels. The delete is refused and nothing is changed.
    /// </summary>
    [Fact]
    public async Task A_workflow_with_more_queued_runs_than_one_delete_cancels_is_refused_whole()
    {
        var admin = await _harness.AdminAsync();
        var id = await _harness.StoreWorkflowAsync();
        var runs = await _harness.SeedRunsAsync(id, Guid.NewGuid(), OneDeleteCancels + 1);

        try
        {
            var res = await admin.DeleteAsync($"/api/workflows/{id}", Ct);
            var body = await res.Content.ReadAsStringAsync(Ct);

            res.StatusCode.Should().Be(HttpStatusCode.Conflict, "{0}", body);
            body.Should().Contain("Switch it off first");

            (await _harness.LoadWorkflowAsync(id)).Should().NotBeNull();

            var after = await _harness.RunsOfAsync(id);
            after.Should().HaveCount(runs.Count);
            after.Should().OnlyContain(r => r.Status == RunStatus.Pending && r.CancelledAt == null,
                "a refused delete cancels none of them");
        }
        finally
        {
            // Left behind they would come due in an hour of suite time and be claimed.
            await using var cleanup = _harness.Store.LightweightSession();
            cleanup.DeleteWhere<WorkflowRun>(r => r.WorkflowDefinitionId == id);
            await cleanup.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>The control for the limit: exactly as many as one delete cancels goes through.</summary>
    [Fact]
    public async Task A_workflow_with_as_many_queued_runs_as_one_delete_cancels_is_deleted()
    {
        var admin = await _harness.AdminAsync();
        var id = await _harness.StoreWorkflowAsync();
        var runs = await _harness.SeedRunsAsync(id, Guid.NewGuid(), OneDeleteCancels);

        var res = await admin.DeleteAsync($"/api/workflows/{id}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "{0}", await res.Content.ReadAsStringAsync(Ct));

        var after = await _harness.RunsOfAsync(id);
        after.Should().HaveCount(runs.Count);
        after.Should().OnlyContain(r => r.Status == RunStatus.Cancelled);
    }

    [Fact]
    public async Task Deleting_a_workflow_that_does_not_exist_is_not_found()
    {
        var admin = await _harness.AdminAsync();

        (await admin.DeleteAsync($"/api/workflows/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_a_workflow_needs_a_token_and_the_capability()
    {
        var id = await _harness.StoreWorkflowAsync();

        (await _harness.Anonymous().DeleteAsync($"/api/workflows/{id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await (await _harness.WithoutTheCapabilityAsync()).DeleteAsync($"/api/workflows/{id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await _harness.LoadWorkflowAsync(id)).Should().NotBeNull("neither caller may have deleted it");

        (await (await _harness.AdminAsync()).DeleteAsync($"/api/workflows/{id}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent,
                "the same request from a caller who holds the capability is served, so the refusals are the gate");
    }
}
