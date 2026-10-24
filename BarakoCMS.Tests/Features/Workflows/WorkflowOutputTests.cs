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
/// An action's outputs: a later action reads the ones the workflow captures, and nothing it did not
/// name is ever written to the run.
/// </summary>
/// <remarks>
/// Runs are seeded and drained with the hosted runner stopped, as in <see cref="WorkflowHaltTests"/>.
/// <see cref="OutputRunnerAction"/> returns a credential among its outputs, so "not stored" is a
/// search of the stored JSON for that value and not only of the typed field.
/// </remarks>
[Collection("Integration1")]
public class WorkflowOutputTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public WorkflowOutputTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    [Fact]
    public async Task A_later_action_reads_the_output_an_earlier_one_captured()
    {
        var run = await DrainAsync(
            Produces("total", "reference"),
            Reads(new() { ["Body"] = "Total {{actions.0.total}}, ref {{ actions.0.reference }}" }));

        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Succeeded);
        run.Actions[0].Outputs.Should().NotBeNull();
        run.Actions[0].Outputs!.Keys.Should().BeEquivalentTo("total", "reference");
        run.Actions[0].Outputs!["total"].Should().Be("42");

        var seen = RecordingRunnerAction.Seen[run.Actions[1].IdempotencyKey];
        seen.Parameters["Body"].Should().Be($"Total 42, ref ref-{run.Actions[0].IdempotencyKey}");
        seen.Outputs.Should().ContainKey(0);
        seen.Outputs[0]["total"].Should().Be("42");
        seen.TriggerEvent.Should().Be(WorkflowEvents.Published);
    }

    [Fact]
    public async Task An_output_the_workflow_does_not_name_never_reaches_the_run_record()
    {
        var run = await DrainAsync(Produces("total"), Reads(new() { ["Body"] = "{{actions.0.total}}" }));

        run.Actions[0].Outputs.Should().NotBeNull();
        run.Actions[0].Outputs!.Should().ContainKey("total");
        run.Actions[0].Outputs!.Should().NotContainKey("secret");

        await using var session = _harness.Store.QuerySession();
        var json = await session.Json.FindByIdAsync<WorkflowRun>(run.Id, Ct);
        json.Should().Contain("\"42\"", "the captured output is stored");
        json.Should().NotContain(OutputRunnerAction.Secret, "a value nobody captured is dropped before the write");
        json.Should().NotContain("xxxxxxxxxx", "nor is one that was not named");
    }

    [Fact]
    public async Task An_action_reading_an_output_nobody_captured_fails_and_does_not_send_it()
    {
        var run = await DrainAsync(Produces("total"), Reads(new() { ["Body"] = "{{actions.0.secret}}" }));

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        run.Actions[1].Status.Should().NotBe(AttemptStatus.Succeeded);
        run.Actions[1].Error.Should().Contain("actions.0.secret");
        RecordingRunnerAction.Seen.Should().NotContainKey(run.Actions[1].IdempotencyKey, "the action never ran");
    }

    [Fact]
    public async Task An_output_too_long_to_keep_is_left_out_and_the_attempt_says_so()
    {
        var run = await DrainAsync(Produces("total", "huge"), Reads(new() { ["Body"] = "{{actions.0.total}}" }));

        run.Actions[0].Outputs.Should().NotBeNull();
        run.Actions[0].Outputs!.Keys.Should().Equal("total");
        run.Actions[0].Error.Should().Contain("huge").And.Contain(ActionOutputs.MaxValueLength.ToString());
    }

    [Fact]
    public async Task An_output_that_looks_like_a_placeholder_is_sent_as_text()
    {
        var contentId = await _harness.StoreContentAsync();
        var run = await DrainAsync(contentId, Produces("inject"), Reads(new() { ["Body"] = "[{{actions.0.inject}}] [{{id}}]" }));

        var seen = RecordingRunnerAction.Seen[run.Actions[1].IdempotencyKey];
        seen.Parameters["Body"].Should().Be($"[{{{{id}}}}] [{contentId}]",
            "the entry's own placeholder resolves, and the output is substituted after, as the text it is");
    }

    [Fact]
    public async Task A_list_field_reaches_an_action_as_json_text_through_the_json_format()
    {
        var lines = new List<object>
        {
            new Dictionary<string, object> { ["ItemCode"] = "PARA-500", ["Quantity"] = 2L, ["UnitCost"] = 1.50m },
            new Dictionary<string, object> { ["ItemCode"] = "AMOX-250", ["Quantity"] = 10L, ["UnitCost"] = 0.1m },
        };
        var contentId = await _harness.StoreContentAsync(new() { ["Lines"] = lines });

        var run = await DrainAsync(contentId, Reads(new() { ["Lines"] = "{{data.Lines | json}}" }));

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
        var received = RecordingRunnerAction.Seen[run.Actions[0].IdempotencyKey].Parameters["Lines"];
        var back = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(received)!;
        back.Select(l => l["ItemCode"].GetString()).Should().Equal("PARA-500", "AMOX-250");
        back.Select(l => l["Quantity"].GetInt64()).Should().Equal(2, 10);
        back.Select(l => l["UnitCost"].GetDecimal()).Should().Equal(1.50m, 0.1m);
    }

    [Fact]
    public async Task An_entry_value_that_looks_like_an_output_reference_is_sent_as_text()
    {
        var contentId = await _harness.StoreContentAsync(new()
        {
            ["Message"] = "leak {{actions.0.reference}} {{actions.7.nothing}}",
        });
        var run = await DrainAsync(contentId,
            Produces("reference"), Reads(new() { ["Body"] = "{{data.Message}} [{{actions.0.reference}}]" }));

        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Succeeded);
        var seen = RecordingRunnerAction.Seen[run.Actions[1].IdempotencyKey];
        seen.Parameters["Body"].Should().Be(
            $"leak {{{{actions.0.reference}}}} {{{{actions.7.nothing}}}} [ref-{run.Actions[0].IdempotencyKey}]",
            "only a reference written in the workflow reads an output, never one an entry supplies");
    }

    [Fact]
    public async Task A_queued_run_copies_the_capture_list_and_the_run_endpoint_serves_the_outputs()
    {
        var admin = await _harness.AdminAsync();
        var contentType = WorkflowStopHarness.NewName("post");

        var saved = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("575-wf"),
            triggerContentType = contentType,
            triggerEvent = "Published",
            actions = new object[]
            {
                new { type = "OutputRunner", capture = new[] { "total" } },
                new { type = "RecordingRunner", parameters = new Dictionary<string, string> { ["Body"] = "{{actions.0.total}}" } },
            },
        }, Ct);
        var text = await saved.Content.ReadAsStringAsync(Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, text);

        using (var doc = JsonDocument.Parse(text))
        {
            doc.RootElement.GetProperty("actions")[0].GetProperty("capture").EnumerateArray()
                .Select(e => e.GetString()).Should().Equal("total");
            doc.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0, "the reference is to a captured output");
        }

        var contentId = await _harness.StoreContentAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var queue = scope.ServiceProvider.GetRequiredService<IWorkflowRunQueue>();
                (await queue.EnqueueAsync(new Content { Id = contentId, ContentType = contentType, Data = new() },
                    WorkflowEvents.Published, Random.Shared.NextInt64(1, long.MaxValue), Ct)).Should().Be(1);
            }

            await _harness.DrainAsync();

            using var saveDoc = JsonDocument.Parse(text);
            var runs = await _harness.RunsOfAsync(saveDoc.RootElement.GetProperty("id").GetGuid());
            runs.Should().ContainSingle();
            run = runs[0];
        });

        run.Actions[0].Capture.Should().Equal("total");
        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Succeeded);

        var served = await admin.GetFromJsonAsync<JsonElement>($"/api/workflow-runs/{run.Id}", Ct);
        var outputs = served.GetProperty("actions")[0].GetProperty("outputs");
        outputs.EnumerateObject().Select(p => p.Name).Should().Equal("total");
        outputs.GetProperty("total").GetString().Should().Be("42");
        served.GetProperty("actions")[1].GetProperty("outputs").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("dotted.name")]
    [InlineData("")]
    public async Task A_capture_name_outside_the_pattern_is_refused(string name)
    {
        var admin = await _harness.AdminAsync();

        var positive = await SaveAsync(admin, ["total"]);
        positive.StatusCode.Should().Be(HttpStatusCode.OK, "the same workflow with a good name saves");

        var refused = await SaveAsync(admin, [name]);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("capture");
    }

    [Fact]
    public async Task A_reference_to_an_output_that_cannot_be_filled_is_warned_about_when_saved()
    {
        var admin = await _harness.AdminAsync();

        var res = await admin.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("575-wf"),
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            actions = new object[]
            {
                new { type = "OutputRunner", capture = new[] { "total" }, parameters = new Dictionary<string, string> { ["Total"] = "{{actions.1.total}}" } },
                new { type = "RecordingRunner", parameters = new Dictionary<string, string> { ["Body"] = "{{actions.0.other}} {{actions.0.total | upper}}" } },
            },
        }, Ct);
        var text = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);

        using var doc = JsonDocument.Parse(text);
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("message").GetString()!).ToList();

        warnings.Should().HaveCount(3);
        warnings.Should().ContainSingle(w => w.Contains("does not run before this one"));
        warnings.Should().ContainSingle(w => w.Contains("does not capture"));
        warnings.Should().ContainSingle(w => w.Contains("only read as it is"));
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string[] capture) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("575-wf"),
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            actions = new object[] { new { type = "OutputRunner", capture } },
        }, Ct);

    private static WorkflowActionAttempt Produces(params string[] capture)
    {
        var attempt = WorkflowStopHarness.Waiting("OutputRunner", due: true);
        attempt.Capture = [.. capture];
        return attempt;
    }

    private static WorkflowActionAttempt Reads(Dictionary<string, string> parameters)
    {
        var attempt = WorkflowStopHarness.Waiting("RecordingRunner", due: true);
        attempt.Parameters = parameters;
        return attempt;
    }

    private async Task<WorkflowRun> DrainAsync(params WorkflowActionAttempt[] attempts) =>
        await DrainAsync(await _harness.StoreContentAsync(), attempts);

    private async Task<WorkflowRun> DrainAsync(Guid contentId, params WorkflowActionAttempt[] attempts)
    {
        var workflowId = await _harness.StoreWorkflowAsync();
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
