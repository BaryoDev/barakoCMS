using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A workflow that is switched off starts nothing, on any trigger, and a workflow stored before it
/// could be switched off still runs.
/// </summary>
/// <remarks>
/// Every disabled workflow here has an enabled twin on the same content type and event, so a count
/// of zero sits beside a count of one from the same call. A queue that fires nothing cannot pass.
/// </remarks>
[Collection("Sequential")]
public class WorkflowEnabledTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowEnabledTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Theory]
    [InlineData("Created")]
    [InlineData("Updated")]
    [InlineData("Published")]
    [InlineData("Unpublished")]
    public async Task A_disabled_workflow_starts_no_run_and_its_enabled_twin_starts_one(string eventType)
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var off = await StoreForAsync(contentType, eventType, enabled: false);
        var on = await StoreForAsync(contentType, eventType, enabled: true);

        (await EnqueueAsync(contentType, eventType)).Should().Be(1, "one of the two is switched off");

        (await _harness.RunsOfAsync(on)).Should().HaveCount(1);
        (await _harness.RunsOfAsync(off)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_workflow_starts_no_Deleted_run_and_its_enabled_twin_starts_one()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var off = await StoreForAsync(contentType, WorkflowEvents.Deleted, enabled: false);
        var on = await StoreForAsync(contentType, WorkflowEvents.Deleted, enabled: true);

        using (var scope = _factory.Services.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            (await queue.QueueDeletedAsync(Guid.NewGuid(), contentType, Ct)).Should().Be(1);
            await session.SaveChangesAsync(Ct);
        }

        (await _harness.RunsOfAsync(on)).Should().HaveCount(1);
        (await _harness.RunsOfAsync(off)).Should().BeEmpty();
    }

    /// <summary>
    /// The projection asks this before it reads the stream to decide an Unpublished event.
    /// </summary>
    [Fact]
    public async Task A_disabled_workflow_is_not_listening_until_it_is_switched_back_on()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var off = await StoreForAsync(contentType, WorkflowEvents.Unpublished, enabled: false);

        (await ListensAsync(contentType, WorkflowEvents.Unpublished)).Should().BeFalse();

        var admin = await _harness.AdminAsync();
        (await SetEnabledAsync(admin, off, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListensAsync(contentType, WorkflowEvents.Unpublished)).Should().BeTrue(
            "the same workflow, switched on, is what the false above was about");
    }

    /// <summary>
    /// A definition saved by an earlier version has no Enabled field at all.
    /// </summary>
    /// <remarks>
    /// A guard, not a test that was red: there was no flag before this change. What it guards is
    /// the flag being read where a missing field means off, which is what a filter in the query
    /// would do.
    /// </remarks>
    [Fact]
    public async Task A_definition_stored_without_the_flag_reads_as_enabled_and_runs()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var id = await StoreForAsync(contentType, WorkflowEvents.Published, enabled: true);
        await StripTheFlagFromTheStoredDocumentAsync(id);

        (await _harness.LoadWorkflowAsync(id))!.Enabled.Should().BeTrue();

        (await EnqueueAsync(contentType, WorkflowEvents.Published)).Should().Be(1);
        (await _harness.RunsOfAsync(id)).Should().HaveCount(1);

        await using var session = _harness.Store.QuerySession();
        (await WorkflowRunner.IsSwitchedOffAsync(session, id, Ct)).Should().BeFalse(
            "the runner must not cancel the runs of a workflow nobody switched off");
    }

    [Fact]
    public async Task A_workflow_created_without_the_flag_is_enabled_and_one_created_with_it_off_starts_nothing()
    {
        var admin = await _harness.AdminAsync();
        var contentType = WorkflowStopHarness.NewName("post");

        var plain = await PostWorkflowAsync(admin, new { name = WorkflowStopHarness.NewName("1009-wf"), triggerContentType = contentType, triggerEvent = "Published" });
        var off = await PostWorkflowAsync(admin, new { name = WorkflowStopHarness.NewName("1009-wf"), triggerContentType = contentType, triggerEvent = "Published", enabled = false });

        plain.GetProperty("enabled").GetBoolean().Should().BeTrue("leaving the field out keeps what a workflow did before it existed");
        off.GetProperty("enabled").GetBoolean().Should().BeFalse();

        (await EnqueueAsync(contentType, WorkflowEvents.Published)).Should().Be(1);
        (await _harness.RunsOfAsync(plain.GetProperty("id").GetGuid())).Should().HaveCount(1);
        (await _harness.RunsOfAsync(off.GetProperty("id").GetGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_a_workflow_off_and_on_is_recorded_once_each_and_a_repeat_is_not()
    {
        var admin = await _harness.AdminAsync();
        var contentType = WorkflowStopHarness.NewName("post");
        var id = await StoreForAsync(contentType, WorkflowEvents.Published, enabled: true);

        var first = await SetEnabledAsync(admin, id, false);
        var body = await first.Content.ReadAsStringAsync(Ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", body);
        using (var doc = JsonDocument.Parse(body))
        {
            doc.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("id").GetGuid().Should().Be(id);
        }

        (await SetEnabledAsync(admin, id, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EnqueueAsync(contentType, WorkflowEvents.Published)).Should().Be(0);

        (await SetEnabledAsync(admin, id, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await EnqueueAsync(contentType, WorkflowEvents.Published)).Should().Be(1, "switched back on, it fires again");

        var disabled = await _harness.AuditOfAsync("workflow.disabled", id);
        disabled.Should().HaveCount(1, "the second request changed nothing, so it recorded nothing");
        disabled[0].ActorUsername.Should().NotBeNullOrEmpty();
        disabled[0].TargetType.Should().Be(nameof(WorkflowDefinition));

        (await _harness.AuditOfAsync("workflow.enabled", id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_body_without_the_flag_is_refused_and_does_not_switch_the_workflow_off()
    {
        var admin = await _harness.AdminAsync();
        var id = await _harness.StoreWorkflowAsync();

        var res = await admin.PutAsJsonAsync($"/api/workflows/{id}/enabled", new { }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _harness.LoadWorkflowAsync(id))!.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Switching_a_workflow_that_does_not_exist_is_not_found()
    {
        var admin = await _harness.AdminAsync();

        (await SetEnabledAsync(admin, Guid.NewGuid(), false)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Switching_a_workflow_needs_a_token_and_the_capability()
    {
        var id = await _harness.StoreWorkflowAsync();

        (await SetEnabledAsync(_harness.Anonymous(), id, false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SetEnabledAsync(await _harness.WithoutTheCapabilityAsync(), id, false))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await _harness.LoadWorkflowAsync(id))!.Enabled.Should().BeTrue("neither caller may have switched it off");

        (await SetEnabledAsync(await _harness.AdminAsync(), id, false)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the same request from a caller who holds the capability is served, so the refusals are the gate");
    }

    /// <summary>
    /// A run that was already queued when its workflow was switched off is cancelled by the runner
    /// and its action never executes. Switching the workflow back on does not bring it back.
    /// </summary>
    [Fact]
    public async Task A_run_queued_before_its_workflow_was_switched_off_is_cancelled_and_never_executes()
    {
        var admin = await _harness.AdminAsync();
        var contentId = await _harness.StoreContentAsync();
        var off = await _harness.StoreWorkflowAsync();
        var on = await _harness.StoreWorkflowAsync();

        var stopped = await _harness.SeedRunAsync(off, contentId, [WorkflowStopHarness.Waiting()]);
        var control = await _harness.SeedRunAsync(on, contentId, [WorkflowStopHarness.Waiting()]);

        (await SetEnabledAsync(admin, off, false)).StatusCode.Should().Be(HttpStatusCode.OK);

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await _harness.MakeDueAsync(stopped.Id);
            await _harness.MakeDueAsync(control.Id);
            await _harness.DrainAsync();
        });

        var after = await _harness.LoadRunAsync(stopped.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Cancelled);
        after.Actions[0].Attempts.Should().Be(0);
        after.Status.Should().Be(RunStatus.Cancelled);
        after.CancelledAt.Should().NotBeNull();
        after.NextDueAt.Should().BeNull("a cancelled run must not come back in the due query");
        WorkflowStopHarness.TimesRun(stopped.Actions[0]).Should().Be(0);

        var ran = await _harness.LoadRunAsync(control.Id);
        ran.Actions.Should().HaveCount(1);
        ran.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, "the runner did run, and ran the one whose workflow is on");
        WorkflowStopHarness.TimesRun(control.Actions[0]).Should().Be(1);

        (await SetEnabledAsync(admin, off, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        await _harness.WithHostedRunnerPausedAsync(_harness.DrainAsync);

        WorkflowStopHarness.TimesRun(stopped.Actions[0]).Should().Be(0, "switching it back on does not send what was waiting");
        (await _harness.LoadRunAsync(stopped.Id)).Status.Should().Be(RunStatus.Cancelled);
    }

    /// <summary>
    /// A retry accepted for a switched off workflow would send nothing: the runner cancels the run
    /// at the next claim, and a cancelled run can never be retried. So it is refused before anything
    /// is written, and the same retry is accepted once the workflow is on.
    /// </summary>
    [Fact]
    public async Task Retrying_an_action_of_a_switched_off_workflows_run_is_refused_until_it_is_switched_on()
    {
        var admin = await _harness.AdminAsync();
        var off = await _harness.StoreWorkflowAsync(w => w.Enabled = false);
        var run = await _harness.SeedRunAsync(off, Guid.NewGuid(), [WorkflowStopHarness.Finished(AttemptStatus.Failed)]);

        var refused = await admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/0/retry", null, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("switched off");

        var after = await _harness.LoadRunAsync(run.Id);
        after.Actions.Should().HaveCount(1);
        after.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        after.Status.Should().Be(RunStatus.Failed, "a refused retry leaves the run as it was, so it can be retried later");
        (await _harness.AuditOfAsync("workflow.action.retried", run.Id)).Should().BeEmpty();

        (await SetEnabledAsync(admin, off, true)).StatusCode.Should().Be(HttpStatusCode.OK);

        try
        {
            var accepted = await admin.PostAsync($"/api/workflow-runs/{run.Id}/actions/0/retry", null, Ct);

            accepted.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await accepted.Content.ReadAsStringAsync(Ct));
            (await _harness.AuditOfAsync("workflow.action.retried", run.Id)).Should().HaveCount(1);
        }
        finally
        {
            // The accepted retry is due at once. Taken out so no runner picks it up after the test.
            await using var cleanup = _harness.Store.LightweightSession();
            cleanup.DeleteWhere<WorkflowRun>(r => r.Id == run.Id);
            await cleanup.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A switch that read the workflow before a delete was saved must not put the workflow back.
    /// </summary>
    /// <remarks>
    /// On two sessions, in the steps the endpoint takes: it loads, changes the flag and updates. A
    /// store there is an upsert and would insert the deleted workflow again. An update of a row that
    /// is gone is refused, and the endpoint answers that refusal with 404.
    /// </remarks>
    [Fact]
    public async Task A_switch_that_read_the_workflow_before_a_delete_is_refused_and_does_not_bring_it_back()
    {
        var id = await _harness.StoreWorkflowAsync();

        await using var switching = _harness.Store.LightweightSession();
        var read = await switching.LoadAsync<WorkflowDefinition>(id, Ct);

        await using (var deleting = _harness.Store.LightweightSession())
        {
            deleting.Delete<WorkflowDefinition>(id);
            await deleting.SaveChangesAsync(Ct);
        }

        read!.Enabled = false;
        switching.Update(read);

        var saving = async () => await switching.SaveChangesAsync(Ct);

        (await saving.Should().ThrowAsync<Exception>())
            .Which.GetType().Name.Should().Contain("NonExistentDocument",
                "the endpoint maps this refusal, by this name, to 404");

        (await _harness.LoadWorkflowAsync(id)).Should().BeNull("the delete stands");
    }

    /// <summary>The inline engine is still a public entry point, and selects definitions the same way.</summary>
    [Fact]
    public async Task The_inline_engine_does_not_run_a_disabled_workflow()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var offKey = WorkflowStopHarness.NewName("engine-off");
        var onKey = WorkflowStopHarness.NewName("engine-on");

        await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Enabled = false;
            w.Actions = [Keyed(offKey)];
        });
        await _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.Actions = [Keyed(onKey)];
        });

        using var scope = _factory.Services.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        await engine.ProcessEventAsync(contentType, WorkflowEvents.Published, NewEntry(contentType), Ct);

        CountingRunnerAction.RunsByKey.GetValueOrDefault(onKey).Should().Be(1);
        CountingRunnerAction.RunsByKey.GetValueOrDefault(offKey).Should().Be(0);
    }

    private static WorkflowAction Keyed(string key) => new()
    {
        Type = WorkflowStopHarness.Counting,
        Parameters = new Dictionary<string, string> { ["IdempotencyKey"] = key },
    };

    private static Content NewEntry(string contentType) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = contentType,
        Data = new Dictionary<string, object>(),
    };

    private Task<Guid> StoreForAsync(string contentType, string eventType, bool enabled) =>
        _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = eventType;
            w.Enabled = enabled;
        });

    /// <summary>What the projection does for one event, without waiting for the daemon.</summary>
    private async Task<int> EnqueueAsync(string contentType, string eventType)
    {
        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

        return await queue.EnqueueAsync(
            NewEntry(contentType), eventType, Random.Shared.NextInt64(1, long.MaxValue), Ct);
    }

    private async Task<bool> ListensAsync(string contentType, string eventType)
    {
        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

        return await queue.ListensAsync(contentType, eventType, Ct);
    }

    private static Task<HttpResponseMessage> SetEnabledAsync(HttpClient client, Guid id, bool enabled) =>
        client.PutAsJsonAsync($"/api/workflows/{id}/enabled", new { enabled }, Ct);

    private static async Task<JsonElement> PostWorkflowAsync(HttpClient client, object trigger)
    {
        var body = JsonSerializer.SerializeToNode(trigger)!.AsObject();
        body["actions"] = JsonSerializer.SerializeToNode(new[]
        {
            new WorkflowAction
            {
                Type = "Email",
                Parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" },
            },
        });

        var res = await client.PostAsJsonAsync("/api/workflows", body, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Removes the flag from a stored workflow's JSON, so the document looks like one saved before
    /// the flag existed.
    /// </summary>
    private async Task StripTheFlagFromTheStoredDocumentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.QueueSqlCommand(
            $"update {store.Options.DatabaseSchemaName}.mt_doc_workflowdefinition "
          + "set data = data - 'Enabled' - 'enabled' "
          + "where id = ?",
            id);
        await session.SaveChangesAsync(Ct);

        var json = await session.Json.FindByIdAsync<WorkflowDefinition>(id);
        json.Should().NotBeNull();
        json.Should().Contain("riggerEvent", "the rest of the document is still there");
        json.Should().NotContain("nabled", "the flag has to be gone for this to be an old document");
    }
}
