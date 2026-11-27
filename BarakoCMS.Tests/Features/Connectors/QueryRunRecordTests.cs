using System.Net.Http.Headers;
using System.Text.Json;
using BarakoCMS.Tests.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// What a run keeps of a Query step: how many rows, and only the outputs the
/// workflow captured. Nobody reading the run sees a value.
/// </summary>
[Collection("Integration1")]
public class QueryRunRecordTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(QueryFlow Flow, WorkflowRun Run, Content Student)> RunAsync(string email, string name)
    {
        var flow = new QueryFlow(factory);
        var student = flow.Student("S-1", email, name);
        await flow.SeedTypesAsync(null, student);
        await flow.SaveQueryAsync();
        var actor = await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, null));
        var enrollment = await flow.EnrollAsync("S-1", actor);

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email"), QueryFlow.Then());
        return (flow, run, student);
    }

    [Fact]
    public async Task The_stored_run_holds_the_count_and_no_id_and_no_value_that_was_not_captured()
    {
        var (flow, run, student) = await RunAsync("kept@example.com", "NeverCaptured");

        await using var session = flow.Store.LightweightSession();
        var stored = await session.Json.FindByIdAsync<WorkflowRun>(run.Id, Ct);

        stored.Should().NotBeNull();
        stored!.Should().Contain("returned 1 entry");
        stored.Should().NotContain(student.Id.ToString(), "the note keeps a count, not the entries' ids");
        stored.Should().NotContain("NeverCaptured", "Name was selected by the query but the workflow did not capture it");
        run.Actions[0].Outputs.Should().BeEquivalentTo(new Dictionary<string, string> { ["Email"] = "kept@example.com" });
        run.Actions[0].Error.Should().NotContain("kept@example.com", "the note says how many, never a value");
    }

    [Fact]
    public async Task A_viewer_of_the_run_is_served_no_value_of_the_step_whoever_they_are()
    {
        var (flow, run, _) = await RunAsync("served@example.com", "Served");

        var admin = await flow.Harness.AdminAsync();
        var asAdmin = await admin.GetAsync($"/api/workflow-runs/{run.Id}", Ct);
        var adminBody = await asAdmin.Content.ReadAsStringAsync(Ct);
        asAdmin.IsSuccessStatusCode.Should().BeTrue(adminBody);
        adminBody.Should().NotContain("served@example.com").And.NotContain("Served");
        using (var doc = JsonDocument.Parse(adminBody))
        {
            var query = doc.RootElement.GetProperty("actions")[0];
            query.GetProperty("actionType").GetString().Should().Be("Query");
            query.TryGetProperty("outputs", out var outputs).Should().BeTrue();
            outputs.ValueKind.Should().Be(JsonValueKind.Null);
            query.GetProperty("error").GetString().Should().Contain("returned 1 entry");
        }

        // Someone who may view runs through the Admin role but holds no read on the type.
        var viewer = factory.CreateClient();
        viewer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await factory.StoredUserTokenAsync(["Admin"]));
        var asViewer = await viewer.GetAsync($"/api/workflow-runs/{run.Id}", Ct);
        (await asViewer.Content.ReadAsStringAsync(Ct)).Should().NotContain("served@example.com");

        var list = await admin.GetAsync($"/api/workflow-runs?workflowId={run.WorkflowDefinitionId}", Ct);
        (await list.Content.ReadAsStringAsync(Ct)).Should().NotContain("served@example.com");
    }

    [Fact]
    public async Task A_kept_value_that_holds_a_template_is_never_rendered_again()
    {
        var flow = new QueryFlow(factory);
        // The enrollment says S-1, so rendering the kept value again would give "S-1".
        await flow.SeedTypesAsync(null, flow.Student("S-1", "{{data.StudentNo}}", "Template"));
        await flow.SaveQueryAsync();
        var enrollment = await flow.EnrollAsync("S-1", await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, null)));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
        QueryFlow.SentTo(run, 1).Should().Be("{{data.StudentNo}}");
    }
}
