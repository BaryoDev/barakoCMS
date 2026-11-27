using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// Everything that finds holes in a template by reading it sees the expression, loop and dataset
/// forms too, one case for each reader. A reader that cannot read a template in full fails closed.
/// </summary>
[Collection("Integration1")]
public class TemplateReaderTests(IntegrationTestFixture fixture)
{
    private static string OverCap => new string('a', TemplateHoles.MaxScanLength) + "{{= 1}}";

    [Theory]
    [InlineData("{{= today()}}", true, false, false)]
    [InlineData("{{= now() | date \"yyyy\"}}", true, false, false)]
    [InlineData("{{= createdAt | money}}", true, false, false)]
    [InlineData("{{= concat(createdBy.name, \"\")}}", false, true, false)]
    [InlineData("{{#each lines}}{{= transition.name}}{{/each}}", false, false, true)]
    [InlineData("{{= 1 + 2}}", false, false, false)]
    public void Needs_reads_the_clock_the_author_and_the_transition_inside_an_expression(
        string template, bool site, bool author, bool transition) =>
        TemplateExpression.Needs([template]).Should().Be((site, author, transition));

    [Theory]
    [InlineData("{{= data.Supplier.Email}}", "Supplier")]
    [InlineData("{{= concat(data.Supplier.Name, \" \", data.Supplier.Email)}}", "Supplier")]
    [InlineData("{{#each data.Runners}}{{data.FirstName}}{{/each}}", "Runners")]
    [InlineData("{{#each lines}}{{= row.Qty * data.Customer.Rate}}{{/each}}", "Customer")]
    public void FollowedFields_names_the_reference_an_expression_or_a_loop_follows(string template, string field) =>
        TemplateExpression.FollowedFields([template]).Should().Equal(field);

    [Fact]
    public void Problems_says_an_expression_that_cannot_be_used_is_sent_as_written()
    {
        TemplateExpression.Problems("a {{= 1 +}} b", onTransition: false).Should().ContainSingle()
            .Which.Should().Contain("holds an expression that cannot be used").And.EndWith("so it is sent as written.");

        TemplateExpression.Problems("{{= round(data.Qty * data.Price, 2) | money}}", onTransition: false).Should().BeEmpty();
    }

    [Theory]
    [InlineData("{{createdBy.email}}")]
    [InlineData("{{transition.by.email}}")]
    [InlineData("{{= createdBy.email}}")]
    [InlineData("{{= concat(\"<\", createdBy.email, \">\")}}")]
    [InlineData("{{= iif(true, transition.by.email, \"\")}}")]
    [InlineData("{{#each lines}}{{= createdBy.email}}{{/each}}")]
    public void NamesAddress_sees_a_user_address_built_in_an_expression_like_the_plain_form(string template) =>
        TemplateExpression.NamesAddress(template).Should().BeTrue();

    [Theory]
    [InlineData("{{= createdBy.name}}")]
    [InlineData("{{= data.Email}}")]
    [InlineData("{{createdBy.name}}")]
    public void NamesAddress_is_false_for_a_template_that_names_no_address(string template) =>
        TemplateExpression.NamesAddress(template).Should().BeFalse();

    [Fact]
    public void NamesAddress_takes_a_template_it_cannot_read_in_full_to_name_one() =>
        TemplateExpression.NamesAddress(OverCap).Should().BeTrue();

    [Fact]
    public void LinkedTransitions_finds_a_link_beside_expressions_and_loops_and_fails_on_an_unreadable_template()
    {
        TemplateExpression.LinkedTransitions("{{= 1 + 2}} {{#each lines}}{{links.transition \"Approve\"}}{{/each}} {{links.action \"Reject\"}}")
            .Should().Equal("Approve", "Reject");

        var act = () => TemplateExpression.LinkedTransitions(OverCap);
        act.Should().Throw<TemplateScanException>();
    }

    [Fact]
    public void UndeclaredTransitionLinks_finds_a_link_beside_expressions_and_loops_and_says_nothing_of_an_unreadable_template()
    {
        TemplateExpression.UndeclaredTransitionLinks(
                "{{= 1 + 2}}{{#each lines}}{{links.transition \"Approve\"}}{{/each}}", lifecycle: null, "invoice")
            .Should().ContainSingle().Which.Should().Contain("Approve");

        TemplateExpression.UndeclaredTransitionLinks(OverCap, lifecycle: null, "invoice").Should().BeEmpty();
    }

    [Fact]
    public void Finish_keeps_an_expression_left_as_written_inside_its_attribute_and_inert()
    {
        var html = EmailTemplateRenderer.Finish("<a title=\"{{= iif(1 < 2, \"a\", \"b\")}}\">x</a><p>{{= 2 > 1}}</p>");

        html.Should().Contain("{{= iif(1 &lt; 2, &quot;a&quot;, &quot;b&quot;)}}").And.Contain("{{= 2 &gt; 1}}");
        html.Should().NotContain("\"a\"");
    }

    [Fact]
    public void Warnings_lists_the_expression_problems_of_each_text_a_template_is_made_of()
    {
        var rendered = new RenderedTemplate("s", string.Empty, [("Body", "{{= 1 +}}"), ("Footer", "{{= 1 + 2}}")]);

        EmailTemplateRenderer.Warnings(rendered, onTransition: false).Should().ContainSingle()
            .Which.Should().Match<(string Field, string Message)>(w => w.Field == "Body" && w.Message.Contains("expression that cannot be used"));
    }

    [Theory]
    [InlineData("UpdateField", "{{createdBy.email}}")]
    [InlineData("UpdateField", "{{= createdBy.email}}")]
    [InlineData("UpdateField", "{{= concat(createdBy.email, \"\")}}")]
    [InlineData("CreateTask", "{{createdBy.email}}")]
    [InlineData("CreateTask", "{{= createdBy.email}}")]
    [InlineData("CreateTask", "{{= concat(createdBy.email, \"\")}}")]
    public void The_workflow_validator_warns_of_an_address_written_into_an_entry_in_every_form(string action, string value)
    {
        var registry = new Mock<IWorkflowPluginRegistry>();
        registry.Setup(r => r.IsActionRegistered(It.IsAny<string>())).Returns(true);
        var validator = new WorkflowSchemaValidator(registry.Object, Mock.Of<IQuerySession>(), null,
            WorkflowTriggerRegistry.BuiltIn, new WorkflowExecutors([]));

        var result = validator.Validate(new WorkflowDefinition
        {
            Name = "address",
            TriggerContentType = "timeEntry",
            TriggerEvent = "Created",
            Actions = [new() { Type = action, Parameters = new() { ["Field"] = "Contact", ["Value"] = value } }],
        });

        result.IsValid.Should().BeTrue("a warning never refuses the save");
        result.Warnings.Should().Contain(w => w.Message.Contains("writes a user's email address"));
    }

    [Fact]
    public void The_workflow_validator_lists_a_broken_expression_with_its_parameter()
    {
        var registry = new Mock<IWorkflowPluginRegistry>();
        registry.Setup(r => r.IsActionRegistered(It.IsAny<string>())).Returns(true);
        var validator = new WorkflowSchemaValidator(registry.Object, Mock.Of<IQuerySession>(), null,
            WorkflowTriggerRegistry.BuiltIn, new WorkflowExecutors([]));

        var result = validator.Validate(new WorkflowDefinition
        {
            Name = "broken",
            TriggerContentType = "timeEntry",
            TriggerEvent = "Created",
            Actions = [new() { Type = "Email", Parameters = new() { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "{{= 1 +}}" } }],
        });

        result.Warnings.Should().ContainSingle(w => w.Field == "actions[0].parameters.Body")
            .Which.Message.Should().Contain("expression that cannot be used");
    }

    [Fact]
    public void A_publish_payload_names_an_expression_a_loop_and_a_row_as_refused_and_a_plain_field_as_read()
    {
        var (refused, fields) = PublishPayload.Placeholders(
            ["{{data.Name}}", "{{= data.Qty * 2}}", "{{#each lines}}{{row.Qty}}{{/each}}", "{{= 1}}"]);

        fields.Should().Equal("Name");
        refused.Should().Equal("{{= data.Qty * 2}}", "{{#each lines}}", "{{row.Qty}}", "{{/each}}", "{{= 1}}");
    }

    [Fact]
    public void A_publish_payload_over_the_scan_cap_is_refused_with_the_limit()
    {
        PublishPayload.Placeholders([OverCap]).Refused.Should().ContainSingle().Which.Should().Contain("262144");
    }

    private static ContentTypeDefinition Schema => new()
    {
        Fields =
        [
            new() { Name = "Secret", DisplayName = "Secret", Type = "text", Sensitivity = SensitivityLevel.Sensitive },
            new() { Name = "Note", DisplayName = "Note", Type = "text", Sensitivity = SensitivityLevel.Public },
        ],
    };

    [Theory]
    [InlineData("{{data.Secret}}")]
    [InlineData("{{data.Secret | upper}}")]
    [InlineData("{{= data.Secret}}")]
    [InlineData("{{= upper(data.Secret) | lower}}")]
    [InlineData("{{#each lines}}{{= row.Qty * len(data.Secret)}}{{/each}}")]
    public void The_egress_check_reads_a_hidden_field_through_every_form(string template)
    {
        TemplateHoles.FirstNonPublic([template], Schema).Should().NotBeNull();
        TemplateHoles.FirstNonPublic(["{{data.Note}} {{= data.Note}}"], Schema).Should().BeNull();
    }

    [Theory]
    [InlineData("{{= createdBy.email}}", "{{= createdBy.email}}")]
    [InlineData("{{#each lines}}x{{/each}}", "{{#each lines}}")]
    [InlineData("{{row.Qty}}", "{{row.Qty}}")]
    public void The_request_check_refuses_the_forms_a_request_cannot_render_and_not_a_plain_one(string template, string hole)
    {
        TemplateHoles.FirstBeyondPlain([template]).Should().Be(hole);
        TemplateHoles.FirstBeyondPlain(["{{data.Note}} {{createdBy.email | upper}}"]).Should().BeNull();
    }

    [Fact]
    public async Task The_variable_picker_lists_the_expression_loop_and_dataset_forms()
    {
        await using var session = fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var extractor = new TemplateVariableExtractor(session, null, null, null);

        var variables = await extractor.GetVariablesAsync("TestType", TestContext.Current.CancellationToken);

        variables.Formats.Select(v => v.Name).Should().Contain(
        [
            "{{= round(data.Qty * data.Price, 2) | money}}",
            "{{#each lines}}{{row.Qty}} x {{row.Product.Name}} {{/each}}",
            "{{= sum(lines.Total) | money}}",
        ]);
    }
}
