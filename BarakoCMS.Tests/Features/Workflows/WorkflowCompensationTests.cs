using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Compensation: what succeeded is undone in reverse when a halting action fails for good or an
/// operator abandons the run, an action with nothing to undo it says so, and a compensation that
/// fails leaves the run visibly incomplete.
/// </summary>
/// <remarks>
/// Driven like <see cref="WorkflowHaltTests"/>: seeded runs drained with the hosted runner stopped.
/// Compensations are <see cref="RecordingRunnerAction"/>, so "undone, last first" is read from the
/// order the action was handed each key, not only from the statuses.
/// </remarks>
[Collection("Sequential")]
public class WorkflowCompensationTests
{
    private const string NoHandler = "NoSuchAction";

    private readonly WorkflowStopHarness _harness;

    public WorkflowCompensationTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task A_halting_failure_undoes_the_actions_that_succeeded_last_first()
    {
        var run = await DrainAsync(
            Undone(Produces("reference"), new() { ["Body"] = "void {{actions.0.reference}}" }),
            Undone(WorkflowStopHarness.Waiting(due: true), new() { ["Body"] = "undo 1" }),
            Halting(WorkflowStopHarness.Waiting(NoHandler, due: true)));

        run.Actions.Select(a => a.Status).Should().Equal(
            AttemptStatus.Succeeded, AttemptStatus.Succeeded, AttemptStatus.Failed);
        run.Actions[0].Compensation!.Status.Should().Be(CompensationStatus.Succeeded);
        run.Actions[1].Compensation!.Status.Should().Be(CompensationStatus.Succeeded);
        run.Actions[2].Compensation.Should().BeNull("an action that failed went nowhere and is not undone");
        run.Compensation.Should().Be(RunCompensation.Compensated);
        run.Status.Should().Be(RunStatus.PartiallyFailed);
        run.NextDueAt.Should().BeNull();

        var keys = run.Actions.Take(2).Select(a => a.Compensation!.IdempotencyKey).ToList();
        keys.Should().OnlyHaveUniqueItems().And.NotContain(run.Actions[0].IdempotencyKey);
        RecordingRunnerAction.Order.Where(keys.Contains).Should().Equal(keys[1], keys[0]);

        RecordingRunnerAction.Seen[keys[0]].Parameters["Body"].Should().Be(
            $"void ref-{run.Actions[0].IdempotencyKey}", "the compensation names what it undoes through the action's output");
        RecordingRunnerAction.Seen[keys[0]].Parameters["CompensationOf"].Should().Be("0");
    }

    [Fact]
    public async Task An_action_with_nothing_to_undo_it_is_reported_as_not_compensated()
    {
        var run = await DrainAsync(
            WorkflowStopHarness.Waiting(due: true),
            Undone(WorkflowStopHarness.Waiting(due: true), new() { ["Body"] = "undo 1" }),
            Halting(WorkflowStopHarness.Waiting(NoHandler, due: true)));

        run.Actions[0].Compensation!.Status.Should().Be(CompensationStatus.NotDeclared);
        run.Actions[1].Compensation!.Status.Should().Be(CompensationStatus.Succeeded);
        run.Compensation.Should().Be(RunCompensation.Incomplete, "something that went out was not undone");
    }

    [Fact]
    public async Task A_compensation_that_fails_leaves_the_run_incomplete_and_the_others_still_run()
    {
        var failing = Undone(WorkflowStopHarness.Waiting(due: true), new());
        failing.Compensate!.Type = NoHandler;

        var run = await DrainAsync(
            Undone(WorkflowStopHarness.Waiting(due: true), new() { ["Body"] = "undo 0" }),
            failing,
            Halting(WorkflowStopHarness.Waiting(NoHandler, due: true)));

        run.Actions[1].Compensation!.Status.Should().Be(CompensationStatus.Failed);
        run.Actions[1].Compensation!.Error.Should().Contain(NoHandler);
        run.Actions[0].Compensation!.Status.Should().Be(CompensationStatus.Succeeded, "one failed undo does not stop the rest");
        run.Compensation.Should().Be(RunCompensation.Incomplete);
    }

    /// <summary>A guard: a workflow that declares no compensation halts exactly as it did.</summary>
    [Fact]
    public async Task A_run_that_declares_no_compensation_halts_as_before()
    {
        var run = await DrainAsync(
            WorkflowStopHarness.Waiting(due: true),
            Halting(WorkflowStopHarness.Waiting(NoHandler, due: true)),
            WorkflowStopHarness.Waiting(due: true));

        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Failed, AttemptStatus.Skipped);
        run.Actions.Should().OnlyContain(a => a.Compensation == null);
        run.Compensation.Should().BeNull();
        run.CompensationStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Abandoning_a_run_undoes_what_succeeded_and_is_audited()
    {
        var admin = await _harness.AdminAsync();
        var seeded = await SeedAbandonableAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/abandon", null, Ct);
            var text = await res.Content.ReadAsStringAsync(Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, text);

            using var doc = JsonDocument.Parse(text);
            doc.RootElement.GetProperty("compensation").GetString().Should().Be("Compensating");
            doc.RootElement.GetProperty("abandonedAt").ValueKind.Should().Be(JsonValueKind.String);
            var undo = doc.RootElement.GetProperty("actions")[0].GetProperty("compensation");
            undo.GetProperty("status").GetString().Should().Be("Pending");
            undo.GetProperty("actionType").GetString().Should().Be("RecordingRunner");

            await _harness.DrainAsync();
            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions[0].Compensation!.Status.Should().Be(CompensationStatus.Succeeded);
        run.Actions[1].Status.Should().Be(AttemptStatus.Unknown, "abandoning does not rewrite what an action did");
        run.Actions[1].Compensation.Should().BeNull();
        run.Actions[2].Status.Should().Be(AttemptStatus.Cancelled, "nothing goes forward after an abandon");
        WorkflowStopHarness.TimesRun(run.Actions[2]).Should().Be(0);
        run.Compensation.Should().Be(RunCompensation.Compensated);

        var audit = await _harness.AuditOfAsync("workflow.run.abandoned", seeded.Id);
        audit.Should().ContainSingle();

        (await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/abandon", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "a run is rolled back once");
        (await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/actions/1/retry", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict, "going forward again would redo what was undone");
    }

    [Fact]
    public async Task Abandoning_needs_the_retry_capability()
    {
        var admin = await _harness.AdminAsync();
        var without = await _harness.WithoutTheCapabilityAsync();
        var refused = await SeedAbandonableAsync();
        var allowed = await SeedAbandonableAsync();

        (await without.PostAsync($"/api/workflow-runs/{refused.Id}/abandon", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await _harness.Anonymous().PostAsync($"/api/workflow-runs/{refused.Id}/abandon", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await _harness.LoadRunAsync(refused.Id)).CompensationStartedAt.Should().BeNull();

        await _harness.WithHostedRunnerPausedAsync(async () =>
            (await admin.PostAsync($"/api/workflow-runs/{allowed.Id}/abandon", null, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK, "the positive control"));
    }

    [Fact]
    public async Task A_run_in_another_tenant_cannot_be_abandoned_from_this_one()
    {
        var admin = await _harness.AdminAsync();
        var here = await SeedAbandonableAsync();
        var there = await SeedAbandonableAsync(tenant: $"t{Guid.NewGuid():N}"[..12]);

        (await admin.PostAsync($"/api/workflow-runs/{there.Id}/abandon", null, Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        await _harness.WithHostedRunnerPausedAsync(async () =>
            (await admin.PostAsync($"/api/workflow-runs/{here.Id}/abandon", null, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK, "the same request on this tenant's run"));
    }

    [Fact]
    public async Task A_run_where_nothing_succeeded_or_something_is_in_flight_is_not_abandoned()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();

        var nothing = await _harness.SeedRunAsync(workflowId, contentId, [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);
        var inFlight = await _harness.SeedRunAsync(workflowId, contentId,
            [Undone(WorkflowStopHarness.Finished(AttemptStatus.Succeeded), new()), WorkflowStopHarness.RunningElsewhere()]);

        (await admin.PostAsync($"/api/workflow-runs/{nothing.Id}/abandon", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PostAsync($"/api/workflow-runs/{inFlight.Id}/abandon", null, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _harness.LoadRunAsync(inFlight.Id)).CompensationStartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_a_run_that_is_being_rolled_back_stops_the_compensations_left()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();

        var seeded = await _harness.SeedRunAsync(workflowId, contentId,
            [Undone(WorkflowStopHarness.Finished(AttemptStatus.Succeeded), new()), WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        await using (var session = _harness.Store.LightweightSession())
        {
            var stored = await session.LoadAsync<WorkflowRun>(seeded.Id, Ct);
            stored!.StartCompensation(DateTimeOffset.UtcNow).Should().Be(1);
            stored.Actions[0].Compensation!.NextAttemptAt = WorkflowStopHarness.ParkedUntil;
            stored.Recompute();
            stored.Status.Should().Be(RunStatus.Running, "a run that is undoing is still at work");
            session.Update(stored);
            await session.SaveChangesAsync(Ct);
        }

        var res = await admin.PostAsync($"/api/workflow-runs/{seeded.Id}/cancel", null, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        var run = await _harness.LoadRunAsync(seeded.Id);
        run.Actions[0].Compensation!.Status.Should().Be(CompensationStatus.Cancelled);
        run.Compensation.Should().Be(RunCompensation.Incomplete);
        run.NextDueAt.Should().BeNull();
    }

    [Fact]
    public async Task A_compensation_is_validated_as_an_action_and_its_secret_is_stored_encrypted_and_not_served()
    {
        var admin = await _harness.AdminAsync();

        var conditional = await SaveAsync(admin, new { type = "Conditional", parameters = new Dictionary<string, string>() });
        conditional.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await conditional.Content.ReadAsStringAsync(Ct)).Should().Contain("compensate");

        var unknown = await SaveAsync(admin, new { type = "NoSuchAction", parameters = new Dictionary<string, string>() });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var saved = await SaveAsync(admin, new
        {
            type = "Webhook",
            parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/void", ["Secret"] = "whsec_compensation_secret" },
        });
        var text = await saved.Content.ReadAsStringAsync(Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, text);
        text.Should().NotContain("whsec_compensation_secret");

        using var doc = JsonDocument.Parse(text);
        var compensate = doc.RootElement.GetProperty("actions")[0].GetProperty("compensate");
        compensate.GetProperty("type").GetString().Should().Be("Webhook");
        compensate.GetProperty("secretSet").GetBoolean().Should().BeTrue();
        compensate.GetProperty("parameters").TryGetProperty("Secret", out _).Should().BeFalse();

        var stored = await _harness.LoadWorkflowAsync(doc.RootElement.GetProperty("id").GetGuid());
        var storedSecret = stored!.Actions[0].Compensate!.Parameters["Secret"];
        WebhookSigning.LooksProtected(storedSecret).Should().BeTrue();
        storedSecret.Should().NotContain("whsec_compensation_secret");
    }

    [Fact]
    public async Task A_run_that_finished_with_every_action_succeeded_is_not_abandoned()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();

        var finished = await _harness.SeedRunAsync(workflowId, contentId,
            [Undone(WorkflowStopHarness.Finished(AttemptStatus.Succeeded), new() { ["Body"] = "undo" })]);
        finished.Status.Should().Be(RunStatus.Succeeded);

        var res = await admin.PostAsync($"/api/workflow-runs/{finished.Id}/abandon", null, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync(Ct));
        (await _harness.LoadRunAsync(finished.Id)).CompensationStartedAt.Should().BeNull();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, object compensate) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("577-wf"),
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            actions = new object[] { new { type = "CountingRunner", compensate } },
        }, Ct);

    private async Task<WorkflowRun> SeedAbandonableAsync(string? tenant = null)
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();

        var unknown = WorkflowStopHarness.Finished(AttemptStatus.Unknown);
        unknown.OnFailure = WorkflowFailurePolicy.Halt;

        var attempts = new List<WorkflowActionAttempt>
        {
            Undone(WorkflowStopHarness.Finished(AttemptStatus.Succeeded), new() { ["Body"] = "undo" }),
            unknown,
            WorkflowStopHarness.Waiting(),
        };

        if (tenant is null)
        {
            return await _harness.SeedRunAsync(workflowId, contentId, attempts);
        }

        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            WorkflowName = "Elsewhere",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = WorkflowEvents.Published,
        };
        for (var i = 0; i < attempts.Count; i++)
        {
            attempts[i].Ordinal = i;
            attempts[i].IdempotencyKey = $"{run.Id:N}-{i}";
            run.Actions.Add(attempts[i]);
        }

        run.Recompute();

        await using var session = _harness.Store.LightweightSession(tenant);
        session.Store(run);
        await session.SaveChangesAsync(Ct);
        return run;
    }

    private static WorkflowActionAttempt Halting(WorkflowActionAttempt attempt)
    {
        attempt.OnFailure = WorkflowFailurePolicy.Halt;
        return attempt;
    }

    private static WorkflowActionAttempt Produces(params string[] capture)
    {
        var attempt = WorkflowStopHarness.Waiting("OutputRunner", due: true);
        attempt.Capture = [.. capture];
        return attempt;
    }

    private static WorkflowActionAttempt Undone(WorkflowActionAttempt attempt, Dictionary<string, string> parameters)
    {
        attempt.Compensate = new WorkflowCompensation { Type = "RecordingRunner", Parameters = parameters };
        return attempt;
    }

    private async Task<WorkflowRun> DrainAsync(params WorkflowActionAttempt[] attempts)
    {
        var workflowId = await _harness.StoreWorkflowAsync();
        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, attempts);
            await _harness.DrainAsync();
            run = await _harness.LoadRunAsync(seeded.Id);
        });

        run.Actions.Should().HaveCount(attempts.Length);
        return run;
    }
}
