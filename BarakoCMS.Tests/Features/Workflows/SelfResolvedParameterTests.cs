using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A module action that declares a parameter in <c>SelfResolvedParameters</c> is handed it as
/// written, from the runner, from a conditional's child and in the dry run preview. Its other
/// parameters are filled as usual.
/// </summary>
[Collection("Sequential")]
public class SelfResolvedParameterTests
{
    private const string Template = "Total {{data.Total}} for {{ data.Owner }}";

    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public SelfResolvedParameterTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static Content Entry() => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "article",
        Status = ContentStatus.Published,
        Data = new Dictionary<string, object> { ["Total"] = "42", ["Owner"] = "Ana" },
    };

    private static IWorkflowPluginRegistry Registry() =>
        new WorkflowPluginRegistry([new SelfResolvingRunnerAction()]);

    [Fact]
    public void The_registry_reports_the_declared_parameters()
    {
        Registry().GetActionMetadata("SelfResolving")!.SelfResolvedParameters.Should().Equal("Raw");
    }

    [Theory]
    [InlineData("Raw")]
    [InlineData("raw")]
    [InlineData("RAW")]
    public void A_declared_parameter_is_left_as_written_in_any_casing(string name)
    {
        var resolved = ActionParameters.Resolve(
            "SelfResolving", new Dictionary<string, string> { [name] = Template, ["Plain"] = Template }, Entry(), Registry());

        resolved[name].Should().Be(Template);
        resolved["Plain"].Should().Be("Total 42 for Ana");
    }

    [Fact]
    public void Without_the_registry_the_same_parameter_is_resolved()
    {
        var resolved = ActionParameters.Resolve(
            "SelfResolving", new Dictionary<string, string> { ["Raw"] = Template }, Entry());

        resolved["Raw"].Should().Be("Total 42 for Ana");
    }

    [Fact]
    public void The_same_parameter_on_another_action_is_still_resolved()
    {
        var resolved = ActionParameters.Resolve(
            "Webhook", new Dictionary<string, string> { ["Raw"] = Template }, Entry(), Registry());

        resolved["Raw"].Should().Be("Total 42 for Ana");
    }

    [Fact]
    public void An_output_reference_in_a_declared_parameter_is_not_masked()
    {
        var parameters = new Dictionary<string, string> { ["Raw"] = "{{actions.0.total}}", ["Plain"] = "{{actions.0.total}}" };

        var (masked, slots) = ActionOutputs.Mask("SelfResolving", parameters, Registry());

        masked["Raw"].Should().Be("{{actions.0.total}}");
        masked["Plain"].Should().NotBe("{{actions.0.total}}");
        slots.All.Should().ContainSingle();
    }

    [Fact]
    public async Task The_runner_hands_the_action_a_declared_parameter_as_written()
    {
        var tag = Guid.NewGuid().ToString();
        var contentId = await _harness.StoreContentAsync(new() { ["Total"] = "42", ["Owner"] = "Ana" });
        var workflowId = await _harness.StoreWorkflowAsync();
        var attempt = WorkflowStopHarness.Waiting("SelfResolving", due: true);
        attempt.Parameters = new() { ["Raw"] = Template, ["Plain"] = Template, ["Tag"] = tag };

        await _harness.WithHostedRunnerPausedAsync(async () =>
        {
            await _harness.SeedRunAsync(workflowId, contentId, [attempt]);
            await _harness.DrainAsync();
        });

        var received = SelfResolvingRunnerAction.ReceivedByTag[tag];
        received["Raw"].Should().Be(Template);
        received["Plain"].Should().Be("Total 42 for Ana");
    }

    [Fact]
    public async Task A_conditionals_child_gets_a_declared_parameter_as_written()
    {
        var tag = Guid.NewGuid().ToString();
        var child = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "SelfResolving", Parameters = new Dictionary<string, string> { ["Raw"] = Template, ["Plain"] = Template, ["Tag"] = tag } },
        });

        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowAction>(new SelfResolvingRunnerAction());
        services.AddSingleton(Registry());
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(
            new Dictionary<string, string> { ["Condition"] = "{{status}} == Published", ["ThenActions"] = child },
            Entry(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        var received = SelfResolvingRunnerAction.ReceivedByTag[tag];
        received["Raw"].Should().Be(Template);
        received["Plain"].Should().Be("Total 42 for Ana");
    }
}
