using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The files a step produced: the runner records the ones an action declares on its attempt, and
/// hands a later action only those of steps that succeeded in the same run.
/// </summary>
/// <remarks>Runs are seeded and drained with the hosted runner stopped, as in <see cref="WorkflowOutputTests"/>.</remarks>
[Collection("Sequential")]
public class WorkflowProducedFileTests
{
    private readonly WorkflowStopHarness _harness;
    private readonly IntegrationTestFixture _factory;

    public WorkflowProducedFileTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static WorkflowActionAttempt Step(string type, params string[] capture)
    {
        var attempt = WorkflowStopHarness.Waiting(type, due: true);
        attempt.Capture = [.. capture];
        return attempt;
    }

    private async Task<WorkflowRun> DrainAsync(params WorkflowActionAttempt[] attempts)
    {
        var contentId = await _harness.StoreContentAsync();
        var workflowId = await _harness.StoreWorkflowAsync();
        WorkflowRun run = null!;

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await _harness.SeedRunAsync(workflowId, contentId, attempts);
            await _harness.DrainAsync();
            run = await _harness.LoadRunAsync(seeded.Id);
        });

        return run;
    }

    [Fact]
    public async Task A_file_a_step_declares_is_recorded_on_its_attempt_and_handed_to_the_next_step()
    {
        var run = await DrainAsync(Step("FileRunner", "FileId"), Step("RecordingRunner"));

        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Succeeded);
        var expected = FileRunnerAction.FileIdFor(run.Actions[0].IdempotencyKey);
        run.Actions[0].ProducedFiles.Should().NotBeNull();
        run.Actions[0].ProducedFiles.Should().Equal(expected);
        run.Actions[1].ProducedFiles.Should().BeNull();

        var seen = RecordingRunnerAction.Seen[run.Actions[1].IdempotencyKey];
        seen.ProducedFiles.Should().ContainKey(0);
        seen.ProducedFiles[0].Should().Equal(expected);
    }

    [Fact]
    public async Task A_file_id_in_an_output_is_not_a_produced_file_unless_the_step_declares_it()
    {
        var run = await DrainAsync(Step("UndeclaredFileRunner", "FileId"), Step("RecordingRunner"));

        run.Actions[0].Outputs.Should().NotBeNull();
        run.Actions[0].Outputs!["FileId"].Should().Be(UndeclaredFileRunnerAction.SomeoneElsesFile.ToString());
        run.Actions[0].ProducedFiles.Should().BeNull();

        var seen = RecordingRunnerAction.Seen[run.Actions[1].IdempotencyKey];
        seen.Outputs.Should().ContainKey(0, "the output is readable, it just does not make the file attachable");
        seen.ProducedFiles.Should().NotContainKey(0, "the step never declared the file");
    }

    [Fact]
    public async Task Files_past_the_limit_are_dropped_and_the_attempt_says_so()
    {
        var run = await DrainAsync(Step("ManyFilesRunner"));

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        run.Actions[0].ProducedFiles.Should().HaveCount(WorkflowActionResult.MaxProducedFiles);
        run.Actions[0].Error.Should().Contain(ManyFilesRunnerAction.Declared.ToString())
            .And.Contain(WorkflowActionResult.MaxProducedFiles.ToString());
    }

    [Fact]
    public async Task A_file_a_step_stored_is_attached_by_the_email_step_that_follows_it()
    {
        var to = $"supplier-{Guid.NewGuid():N}@example.com";
        var email = Step("Email");
        email.Parameters = new Dictionary<string, string>
        {
            ["To"] = to,
            ["Subject"] = "Purchase order",
            ["Body"] = "Attached.",
            ["Attachments"] = "{{actions.0.FileId}}",
        };

        var run = await DrainAsync(Step("SavedFileRunner", "FileId"), email);

        run.Actions.Select(a => a.Status).Should().Equal(
            [AttemptStatus.Succeeded, AttemptStatus.Succeeded], run.Actions[1].Error);
        var sent = _factory.Email.Messages.Where(m => m.To == to).ToList();
        sent.Should().ContainSingle();
        sent[0].Attachments.Should().HaveCount(1);
        sent[0].Attachments[0].FileName.Should().Be("po-1042.png");
        sent[0].Attachments[0].Content.Take(8).Should().Equal(FileSamples.Png().Take(8));
    }

    [Fact]
    public async Task An_action_that_returns_no_file_list_succeeds_with_no_files()
    {
        var run = await DrainAsync(Step("NullFilesRunner"));

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
        run.Actions[0].ProducedFiles.Should().BeNull();
    }

    [Fact]
    public async Task A_file_is_recorded_even_when_the_workflow_captures_no_output()
    {
        var run = await DrainAsync(Step("FileRunner"), Step("RecordingRunner"));

        run.Actions[0].Outputs.Should().BeNull();
        run.Actions[0].ProducedFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task An_email_that_reads_a_captured_file_output_saves_without_a_warning()
    {
        var admin = await _harness.AdminAsync();

        var saved = await SaveAsync(admin, capture: ["FileId"]);
        var text = await saved.Content.ReadAsStringAsync(Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, text);

        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0, text);

        var uncaptured = await SaveAsync(admin, capture: []);
        var uncapturedText = await uncaptured.Content.ReadAsStringAsync(Ct);
        uncaptured.StatusCode.Should().Be(HttpStatusCode.OK, uncapturedText);
        using var other = JsonDocument.Parse(uncapturedText);
        other.RootElement.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("message").GetString()!)
            .Should().ContainSingle(m => m.Contains("does not capture"));
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, string[] capture) =>
        client.PostAsJsonAsync("/api/workflows", new
        {
            name = WorkflowStopHarness.NewName("1198-wf"),
            triggerContentType = WorkflowStopHarness.NewName("order"),
            triggerEvent = "Published",
            actions = new object[]
            {
                new { type = "FileRunner", capture },
                new
                {
                    type = "Email",
                    parameters = new Dictionary<string, string>
                    {
                        ["To"] = "supplier@example.com",
                        ["Subject"] = "Purchase order",
                        ["Body"] = "Attached.",
                        ["Attachments"] = "{{actions.0.FileId}}",
                    },
                },
            },
        }, Ct);
}
