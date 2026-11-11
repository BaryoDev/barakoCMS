using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Moq;

namespace BarakoCMS.Tests.Features.WorkflowRuns;

/// <summary>An action's label is stored on the definition, copied onto the run when it is queued, and checked on save.</summary>
[Collection("Integration1")]
public class WorkflowRunActionLabelTests
{
    private readonly WorkflowStopHarness _harness;

    public WorkflowRunActionLabelTests(IntegrationTestFixture factory) => _harness = new WorkflowStopHarness(factory);

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static WorkflowSchemaValidator ValidatorOver(string actionType)
    {
        var registry = new Mock<IWorkflowPluginRegistry>();
        registry.Setup(r => r.IsActionRegistered(actionType)).Returns(true);
        registry.Setup(r => r.GetActionMetadata(actionType)).Returns(new WorkflowActionMetadata { Type = actionType });
        registry.Setup(r => r.GetAllActions()).Returns([new WorkflowActionMetadata { Type = actionType }]);
        return new WorkflowSchemaValidator(registry.Object, Mock.Of<IQuerySession>());
    }

    private static WorkflowDefinition WorkflowWith(string? label) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Labelled",
        TriggerContentType = "article",
        TriggerEvent = WorkflowEvents.Published,
        Actions = [new WorkflowAction { Type = "Email", Label = label }],
    };

    [Fact]
    public void A_run_copies_the_label_of_each_action_when_it_is_queued()
    {
        var workflow = WorkflowWith("Tell the editor");
        workflow.Actions.Add(new WorkflowAction { Type = "Email" });

        var run = WorkflowRunQueue.NewRun(workflow, Guid.NewGuid(), "article", WorkflowEvents.Published, 1);

        run.Actions.Select(a => a.Label).Should().Equal("Tell the editor", null);
    }

    [Fact]
    public async Task Renaming_the_label_on_the_definition_leaves_an_earlier_run_as_it_was()
    {
        var workflow = WorkflowWith("Tell the editor");
        var run = WorkflowRunQueue.NewRun(workflow, Guid.NewGuid(), "article", WorkflowEvents.Published, 1);
        await using (var session = _harness.Store.LightweightSession())
        {
            session.Store(workflow);
            session.Store(run);
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = _harness.Store.LightweightSession())
        {
            var stored = await session.LoadAsync<WorkflowDefinition>(workflow.Id, Ct);
            stored!.Actions[0].Label = "Something else";
            session.Store(stored);
            await session.SaveChangesAsync(Ct);
        }

        var admin = await _harness.AdminAsync();
        var res = await admin.GetAsync($"/api/workflow-runs/{run.Id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("actions")[0].GetProperty("label").GetString().Should().Be("Tell the editor");

        var renamed = await _harness.LoadWorkflowAsync(workflow.Id);
        renamed!.Actions[0].Label.Should().Be("Something else");
    }

    [Fact]
    public async Task A_run_made_before_labels_has_a_null_label()
    {
        var content = await _harness.StoreContentAsync();
        var run = await _harness.SeedRunAsync(Guid.NewGuid(), content, [WorkflowStopHarness.Waiting()]);

        var admin = await _harness.AdminAsync();
        using var doc = JsonDocument.Parse(await admin.GetStringAsync($"/api/workflow-runs/{run.Id}", Ct));

        doc.RootElement.GetProperty("actions")[0].GetProperty("label").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("Tell the editor")]
    [InlineData(null)]
    public void A_plain_label_or_none_is_accepted(string? label)
    {
        var result = ValidatorOver("Email").Validate(WorkflowWith(label));

        result.Errors.Should().NotContain(e => e.Field.EndsWith(".label"));
    }

    [Theory]
    [InlineData("<b>Tell</b> the editor")]
    [InlineData("a > b")]
    [InlineData("two\nlines")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("   ")]
    [InlineData("")]
    public void A_label_with_markup_control_characters_or_no_text_is_refused(string label)
    {
        var result = ValidatorOver("Email").Validate(WorkflowWith(label));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Field == "actions[0].label");
    }

    [Fact]
    public void A_label_over_the_cap_is_refused_and_one_at_the_cap_is_not()
    {
        var atCap = ValidatorOver("Email").Validate(WorkflowWith(new string('a', WorkflowSchemaValidator.MaxLabelLength)));
        var over = ValidatorOver("Email").Validate(WorkflowWith(new string('a', WorkflowSchemaValidator.MaxLabelLength + 1)));

        atCap.Errors.Should().NotContain(e => e.Field == "actions[0].label");
        over.Errors.Should().Contain(e => e.Field == "actions[0].label");
    }
}
