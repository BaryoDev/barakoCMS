using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A workflow stored with <c>conditions: null</c>, or fired by an entry with no data, is read as
/// having no conditions to meet, and a save never stores the null.
/// </summary>
/// <remarks>
/// Each null workflow has a twin that is plainly fine on the same type and event, so the count of
/// runs tells a queue that threw (none queued, the twin lost with it) from one that worked.
/// </remarks>
[Collection("Sequential")]
public class WorkflowNullConditionsTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowNullConditionsTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task A_workflow_stored_with_null_conditions_is_queued_and_its_action_runs()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var nulled = await StoreForAsync(contentType, w => w.Conditions = null!);
        var plain = await StoreForAsync(contentType, _ => { });

        (await _harness.LoadWorkflowAsync(nulled))!.Conditions.Should().BeNull("the stored document has to carry the null");

        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            (await EnqueueAsync(Entry(contentId, contentType, new Dictionary<string, object>()))).Should().Be(2);
            await _harness.DrainAsync();

            var runs = await _harness.RunsOfAsync(nulled);
            runs.Should().HaveCount(1);
            run = runs[0];
        });

        (await _harness.RunsOfAsync(plain)).Should().HaveCount(1);
        run.Actions.Should().HaveCount(1);
        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        WorkflowStopHarness.TimesRun(run.Actions[0]).Should().Be(1, "the action ran");
    }

    [Fact]
    public async Task An_entry_with_no_data_fails_a_field_condition_without_losing_the_other_workflows()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var conditioned = await StoreForAsync(contentType,
            w => w.Conditions = new Dictionary<string, string> { ["Region"] = "north" });
        var plain = await StoreForAsync(contentType, _ => { });

        (await EnqueueAsync(Entry(Guid.NewGuid(), contentType, data: null))).Should().Be(1);

        (await _harness.RunsOfAsync(conditioned)).Should().BeEmpty("an entry with no data has no Region");
        (await _harness.RunsOfAsync(plain)).Should().HaveCount(1);
    }

    [Fact]
    public async Task The_engine_runs_the_actions_of_a_workflow_stored_with_null_conditions()
    {
        var contentType = WorkflowStopHarness.NewName("post");
        var key = $"1118-{Guid.NewGuid():N}";
        await StoreForAsync(contentType, w =>
        {
            w.Conditions = null!;
            w.Actions = [new WorkflowAction
            {
                Type = WorkflowStopHarness.Counting,
                Parameters = new Dictionary<string, string> { ["IdempotencyKey"] = key },
            }];
        });

        using var scope = _factory.Services.CreateScope();
#pragma warning disable CS0618 // the engine is what is under test
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
#pragma warning restore CS0618

        await engine.ProcessEventAsync(contentType, WorkflowEvents.Published,
            Entry(Guid.NewGuid(), contentType, new Dictionary<string, object>()), Ct);

        CountingRunnerAction.RunsByKey.GetValueOrDefault(key).Should().Be(1);
    }

    [Fact]
    public async Task A_workflow_saved_with_null_conditions_is_stored_with_none()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("1118-wf"),
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            conditions = (Dictionary<string, string>?)null,
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" } } },
        }, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("conditions").ValueKind.Should().Be(JsonValueKind.Object);

        var stored = await _harness.LoadWorkflowAsync(doc.RootElement.GetProperty("id").GetGuid());
        stored.Should().NotBeNull();
        stored!.Conditions.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// The same kind of document with no actions list: the credential pass and the list read every
    /// stored workflow, so one such document used to stop both for the whole tenant.
    /// </summary>
    [Fact]
    public async Task A_workflow_stored_with_null_actions_stops_neither_the_credential_pass_nor_the_list()
    {
        // Named to sort first, so the first page of the list reads it.
        var nulled = await _harness.StoreWorkflowAsync(w =>
        {
            w.Name = $"0000-1118-{Guid.NewGuid():N}";
            w.Actions = null!;
        });

        try
        {
            var clear = await _harness.StoreWorkflowAsync(w => w.Actions =
            [
                new WorkflowAction { Type = "CredentialEcho", Parameters = new() { ["ApiKey"] = "ak_live_1118_clear" } },
            ]);

            (await _harness.LoadWorkflowAsync(nulled))!.Actions.Should().BeNull("the stored document has to carry the null");

            using (var scope = _factory.Services.CreateScope())
            await using (var session = _harness.Store.LightweightSession())
            {
                var protector = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Security.ISecretProtector>();
                var pass = () => WorkflowCredentialMigrationService.ProtectStoredAsync(
                    session, protector, Ct, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

                await pass.Should().NotThrowAsync();
            }

            (await _harness.LoadWorkflowAsync(clear))!.Actions[0].Parameters["ApiKey"]
                .Should().NotBe("ak_live_1118_clear", "the workflow after it in the pass is still encrypted");

            var admin = await _harness.AdminAsync();
            var list = await admin.GetAsync("/api/workflows?pageSize=100", Ct);
            list.IsSuccessStatusCode.Should().BeTrue("got {0}", list.StatusCode);
        }
        finally
        {
            await using var session = _harness.Store.LightweightSession();
            session.Delete<WorkflowDefinition>(nulled);
            await session.SaveChangesAsync(Ct);
        }
    }

    private Task<Guid> StoreForAsync(string contentType, Action<WorkflowDefinition> shape) =>
        _harness.StoreWorkflowAsync(w =>
        {
            w.TriggerContentType = contentType;
            w.TriggerEvent = WorkflowEvents.Published;
            shape(w);
        });

    private static Content Entry(Guid id, string contentType, Dictionary<string, object>? data) => new()
    {
        Id = id,
        ContentType = contentType,
        Status = ContentStatus.Published,
        Data = data!,
    };

    private async Task<int> EnqueueAsync(Content entry)
    {
        using var scope = _factory.Services.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();

        return await queue.EnqueueAsync(entry, WorkflowEvents.Published, Random.Shared.NextInt64(1, long.MaxValue), Ct);
    }
}
