using System.Net;
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

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await _harness.MakeDueAsync(other.Id);
            await _harness.DrainAsync();
        });

        WorkflowStopHarness.TimesRun(waiting.Actions[0]).Should().Be(0, "a cancelled run's action never executes");
        WorkflowStopHarness.TimesRun(inFlight.Actions[1]).Should().Be(0);
        WorkflowStopHarness.TimesRun(other.Actions[0]).Should().Be(1, "the runner did run, and ran the kept workflow's run");
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
