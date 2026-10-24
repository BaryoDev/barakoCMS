using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// CreateContent faces the type's rules, numbers the entry, and creates it once however often an
/// attempt runs.
/// </summary>
[Collection("Integration1")]
public class CreateContentActionTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Same_idempotency_key_creates_one_entry_with_source_links()
    {
        var type = await StoreTypeAsync();
        var runId = Guid.CreateVersion7();
        var key = $"{runId:N}-0";

        var first = await RunAsync(Parameters(type, runId, key));
        var second = await RunAsync(Parameters(type, runId, key));

        first.Succeeded.Should().BeTrue(first.Error);
        second.Succeeded.Should().BeTrue(second.Error);
        second.Outputs["id"].Should().Be(first.Outputs["id"]);
        first.Outputs["number"].Should().Be("T-001");
        first.Created.Should().ContainSingle(c => c.ContentType == type && c.Number == "T-001");

        using var scope = fixture.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);
        all.Should().ContainSingle();
        all[0].SourceRunId.Should().Be(runId);
        all[0].LifecycleState.Should().Be("Waiting");
    }

    [Fact]
    public async Task An_existing_entry_of_another_type_under_the_id_is_a_permanent_failure_not_a_hit()
    {
        var type = await StoreTypeAsync();
        var other = await StoreTypeAsync();
        var runId = Guid.CreateVersion7();
        var first = await RunAsync(Parameters(type, runId, $"{runId:N}-0"));

        var clash = Parameters(other, runId, "different-key");
        clash["Id"] = first.Outputs["id"];
        var result = await RunAsync(clash);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("not made by this action");
    }

    [Fact]
    public async Task An_existing_entry_made_by_another_run_under_the_id_is_a_permanent_failure()
    {
        var type = await StoreTypeAsync();
        var runId = Guid.CreateVersion7();
        var first = await RunAsync(Parameters(type, runId, $"{runId:N}-0"));

        var clash = Parameters(type, Guid.CreateVersion7(), "another-key");
        clash["Id"] = first.Outputs["id"];
        var result = await RunAsync(clash);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task An_existing_entry_of_another_branch_under_the_id_is_a_permanent_failure()
    {
        var type = await StoreTypeAsync();
        var runId = Guid.CreateVersion7();
        var key = $"{runId:N}-0";
        var first = await RunAsync(Parameters(type, runId, key));

        var elsewhere = new Content { Id = Guid.NewGuid(), ContentType = "Order", BranchId = Guid.CreateVersion7() };
        var result = await RunAsync(Parameters(type, runId, key), elsewhere);

        first.Succeeded.Should().BeTrue(first.Error);
        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_rule_is_a_permanent_failure_and_creates_nothing()
    {
        var type = await StoreTypeAsync();
        var parameters = Parameters(type, Guid.CreateVersion7(), "k1");
        parameters["Data.Name"] = "";

        var result = await RunAsync(parameters);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Be("Name is required.");

        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .Query<Content>().AnyAsync(c => c.ContentType == type, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_status_parameter_is_ignored_and_the_entry_starts_as_a_draft()
    {
        var type = await StoreTypeAsync();
        var parameters = Parameters(type, Guid.CreateVersion7(), "k-status");
        parameters["Status"] = "Published";

        var result = await RunAsync(parameters);

        result.Succeeded.Should().BeTrue(result.Error);
        using var scope = fixture.Services.CreateScope();
        var made = await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .LoadAsync<Content>(Guid.Parse(result.Outputs["id"]), Ct);
        made!.Status.Should().Be(ContentStatus.Draft);
    }

    [Fact]
    public async Task The_new_entry_takes_the_branch_of_the_triggering_entry()
    {
        var type = await StoreTypeAsync();
        var branch = Guid.CreateVersion7();
        var parameters = Parameters(type, Guid.CreateVersion7(), "k-branch");

        var result = await RunAsync(parameters, new Content { Id = Guid.NewGuid(), ContentType = "Order", BranchId = branch });

        result.Succeeded.Should().BeTrue(result.Error);
        using var scope = fixture.Services.CreateScope();
        var made = await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .LoadAsync<Content>(Guid.Parse(result.Outputs["id"]), Ct);
        made!.BranchId.Should().Be(branch);
    }

    [Fact]
    public async Task A_branch_series_numbers_from_the_code_of_the_triggering_entrys_branch()
    {
        var type = await StoreTypeAsync(seriesTemplate: "{branch}.ticket");
        var branch = Guid.CreateVersion7();
        var code = "b" + Guid.NewGuid().ToString("n")[..6];
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = branch,
                ContentType = "Branch",
                Data = new Dictionary<string, object> { ["Code"] = code },
            });
            await session.SaveChangesAsync(Ct);
        }

        var result = await RunAsync(Parameters(type, Guid.CreateVersion7(), "k-branch-series"),
            new Content { Id = Guid.NewGuid(), ContentType = "Order", BranchId = branch });

        result.Succeeded.Should().BeTrue(result.Error);
        result.Outputs["number"].Should().Be("T-001");
    }

    [Fact]
    public async Task Through_the_runner_the_attempt_keeps_the_entry_it_created()
    {
        var type = await StoreTypeAsync();
        var harness = new WorkflowStopHarness(fixture);
        var contentId = await harness.StoreContentAsync();
        var workflowId = await harness.StoreWorkflowAsync();
        var attempt = WorkflowStopHarness.Waiting("CreateContent", due: true);
        attempt.Parameters = new() { ["ContentType"] = type, ["Data.Name"] = "From a run" };
        WorkflowRun run = null!;

        await harness.WithHostedRunnerPausedAsync(async () =>
        {
            var seeded = await harness.SeedRunAsync(workflowId, contentId, [attempt]);
            await RunnerDrain.DrainAsync(fixture.Services, Ct);
            run = await harness.LoadRunAsync(seeded.Id);
        });

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
        var created = run.Actions[0].Created.Should().ContainSingle().Subject;
        using var scope = fixture.Services.CreateScope();
        var made = await scope.ServiceProvider.GetRequiredService<IDocumentSession>().LoadAsync<Content>(created.Id, Ct);
        made!.SourceRunId.Should().Be(run.Id);
    }

    [Fact]
    public async Task A_numbering_refusal_is_a_permanent_failure_and_creates_nothing()
    {
        var type = await StoreTypeAsync(seriesTemplate: "{branch}.ticket");

        // The subject has no branch, so a branch series has no code to number from.
        var result = await RunAsync(Parameters(type, Guid.CreateVersion7(), "k-numbering"));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Be("The numbering needs a branch with a code.");

        using var scope = fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .Query<Content>().AnyAsync(c => c.ContentType == type, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_number_already_issued_is_refused_however_it_is_spelled()
    {
        var type = await StoreTypeAsync(clientAllocated: true);
        var first = await RunAsync(Parameters(type, Guid.CreateVersion7(), "k-issued-1"));
        first.Outputs["number"].Should().Be("T-001");

        foreach (var (spelling, reason) in new[] { ("T-001", "That number is already issued."), ("T-0001", "That number does not fit the series.") })
        {
            var parameters = Parameters(type, Guid.CreateVersion7(), "k-issued-" + spelling);
            parameters["Data.No"] = spelling;

            var again = await RunAsync(parameters);

            again.Succeeded.Should().BeFalse(spelling);
            again.Error.Should().Be(reason, spelling);
        }
    }

    [Fact]
    public async Task Json_text_for_a_json_field_is_stored_as_the_list_or_object_it_holds()
    {
        var type = await StoreTypeAsync();
        var parameters = Parameters(type, Guid.CreateVersion7(), "k-json");
        parameters["Data.Lines"] = "{{data.Src}}";
        parameters["Data.Meta"] = "{{data.Ward}}";
        var source = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "Order",
            Data = new()
            {
                ["Src"] = new List<object> { new Dictionary<string, object> { ["Code"] = "PARA", ["Quantity"] = 2L } },
                ["Ward"] = new Dictionary<string, object> { ["Ward"] = "A" },
            },
        };

        var result = await RunAsync(ActionParameters.Resolve("CreateContent", parameters, source), source);

        result.Succeeded.Should().BeTrue(result.Error);
        using var scope = fixture.Services.CreateScope();
        var made = await scope.ServiceProvider.GetRequiredService<IDocumentSession>()
            .LoadAsync<Content>(Guid.Parse(result.Outputs["id"]), Ct);
        var lines = made!.Data["Lines"].Should().BeAssignableTo<List<object>>().Subject;
        lines.Should().ContainSingle().Which.Should().BeAssignableTo<Dictionary<string, object>>()
            .Which["Quantity"].Should().Be(2L);
        made.Data["Meta"].Should().BeAssignableTo<Dictionary<string, object>>().Which["Ward"].Should().Be("A");
        made.Data["Name"].Should().Be("Sample entry", "a text field keeps its text");
    }

    [Fact]
    public async Task Text_that_is_not_json_for_a_json_field_is_a_permanent_failure()
    {
        var type = await StoreTypeAsync();
        var parameters = Parameters(type, Guid.CreateVersion7(), "k-badjson");
        parameters["Data.Lines"] = "{{data.Src}}";
        var source = new Content { Id = Guid.NewGuid(), ContentType = "Order", Data = new() { ["Src"] = "not json" } };

        var result = await RunAsync(ActionParameters.Resolve("CreateContent", parameters, source), source);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Lines").And.NotContain("not json");
    }

    [Fact]
    public async Task A_template_mixing_JSON_with_a_value_cannot_add_keys_to_a_json_field()
    {
        var type = await StoreTypeAsync();
        var source = new Content { Id = Guid.NewGuid(), ContentType = "Order", Data = new() { ["Name"] = "x\",\"admin\":true,\"y\":\"" } };
        var template = Parameters(type, Guid.CreateVersion7(), "k-inject");
        template["Data.Lines"] = "{\"note\":\"{{data.Name}}\"}";

        var result = await RunAsync(ActionParameters.Resolve("CreateContent", template, source), source);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Lines").And.NotContain("admin");
    }

    private static Dictionary<string, string> Parameters(string type, Guid runId, string key) => new()
    {
        ["ContentType"] = type,
        ["Data.Name"] = "Sample entry",
        ["IdempotencyKey"] = key,
        ["RunId"] = runId.ToString(),
    };

    private async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content? subject = null)
    {
        using var scope = fixture.Services.CreateScope();
        var action = ActivatorUtilities.CreateInstance<CreateContentAction>(scope.ServiceProvider);
        return await action.RunAsync(parameters, subject ?? new Content { Id = Guid.NewGuid(), ContentType = "Order" }, Ct);
    }

    /// <summary>A numbered type with a lifecycle and a rule that refuses an empty Name.</summary>
    private async Task<string> StoreTypeAsync(string? seriesTemplate = null, bool clientAllocated = false)
    {
        var name = "ticket-" + Guid.NewGuid().ToString("n")[..10];
        var rule = Rule("name", Cond("Name", "notEmpty"));
        rule.Message = "Name is required.";

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields =
            [
                new FieldDefinition { Name = "No", DisplayName = "No", Type = "string" },
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition { Name = "Lines", DisplayName = "Lines", Type = "json" },
                new FieldDefinition { Name = "Meta", DisplayName = "Meta", Type = "object" },
            ],
            Numbering = new NumberingRule { Field = "No", Prefix = "T-", Padding = 3, SeriesTemplate = seriesTemplate, ClientAllocated = clientAllocated },
            Lifecycle = new LifecycleDefinition { States = ["Waiting", "Done"], InitialState = "Waiting" },
            Rules = [rule],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }
}
