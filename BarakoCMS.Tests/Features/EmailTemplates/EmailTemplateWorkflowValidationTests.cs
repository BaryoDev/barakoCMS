using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>An Email action takes its subject and body from a template or from its own parameters, one or the other.</summary>
public class EmailTemplateWorkflowValidationTests
{
    private readonly WorkflowSchemaValidator _validator;

    public EmailTemplateWorkflowValidationTests()
    {
        var registry = new Mock<IWorkflowPluginRegistry>();
        registry.Setup(r => r.IsActionRegistered("Email")).Returns(true);
        registry.Setup(r => r.GetActionMetadata("Email")).Returns(new WorkflowActionMetadata
        {
            Type = "Email",
            RequiredParameters = ["To", "Subject", "Body"],
        });
        _validator = new WorkflowSchemaValidator(registry.Object, Mock.Of<Marten.IQuerySession>());
    }

    private static WorkflowDefinition Workflow(Dictionary<string, string> parameters) => new()
    {
        Name = "templates",
        TriggerContentType = "signup",
        TriggerEvent = "Created",
        Actions = [new WorkflowAction { Type = "Email", Parameters = parameters }],
    };

    /// <summary>Red before templates: Subject and Body were required whatever else the action named.</summary>
    [Fact]
    public void An_email_naming_a_template_needs_no_subject_or_body()
    {
        var result = _validator.Validate(Workflow(new() { ["To"] = "a@example.com", ["Template"] = "welcome" }), TestContext.Current.CancellationToken);

        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors.Select(e => e.Message)));
    }

    [Theory]
    [InlineData("Subject")]
    [InlineData("Body")]
    public void An_email_naming_a_template_and_writing_its_own_text_is_refused(string written)
    {
        var result = _validator.Validate(Workflow(new() { ["To"] = "a@example.com", ["Template"] = "welcome", [written] = "text" }), TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Field.Should().Be("actions[0].parameters.Template");
    }

    /// <summary>A guard that passes both ways: without a template, the two are as required as they were.</summary>
    [Fact]
    public void An_email_without_a_template_still_needs_its_subject_and_body()
    {
        var result = _validator.Validate(Workflow(new() { ["To"] = "a@example.com", ["Template"] = " " }), TestContext.Current.CancellationToken);

        result.IsValid.Should().BeFalse();
        result.Errors.Select(e => e.Field).Should().BeEquivalentTo(["actions[0].parameters.Subject", "actions[0].parameters.Body"]);
    }
}
