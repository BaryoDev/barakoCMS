using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

public class ModuleWorkflowActionRegistryTests
{
    private sealed class EchoAction : IWorkflowAction
    {
        public string Type => "ignored";
        [Obsolete("test")]
        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) => Task.CompletedTask;
        public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
            Task.FromResult(WorkflowActionResult.Success(new Dictionary<string, string> { ["echo"] = "yes" }));
    }

    private sealed class Mod(string name) : IBarakoModule
    {
        public string Name { get; } = name;
        public string Version => "1.0.0";
        public void ConfigureServices(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration) { }
    }

    private static IModuleWorkflowActions For(ModuleWorkflowActionRegistry registry, IServiceCollection services, string module) =>
        registry.For(services, new Mod(module));

    [Fact]
    public async Task A_prefixed_type_registers_and_reports_the_module_type()
    {
        var services = new ServiceCollection();
        For(new ModuleWorkflowActionRegistry(), services, "Shop").Add<EchoAction>("Shop.Echo");

        using var provider = services.BuildServiceProvider();
        var action = provider.GetServices<IWorkflowAction>().Single();
        action.Type.Should().Be("Shop.Echo");
        var result = await action.RunAsync(new Dictionary<string, string>(), new Content(), default);
        result.Succeeded.Should().BeTrue();
        result.Outputs!["echo"].Should().Be("yes");
    }

    [Theory]
    [InlineData("Echo")]
    [InlineData("Other.Echo")]
    [InlineData("Shop.")]
    [InlineData("")]
    [InlineData("shop.Echo")]
    public void A_type_outside_the_module_prefix_is_refused(string type)
    {
        var act = () => For(new ModuleWorkflowActionRegistry(), new ServiceCollection(), "Shop").Add<EchoAction>(type);
        act.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("Shop");
    }

    [Theory]
    [InlineData("Email")]
    [InlineData("Conditional")]
    public void A_core_type_cannot_be_taken(string core)
    {
        // A module named after a core type would pass the prefix test, so the clash check runs on its own.
        var act = () => For(new ModuleWorkflowActionRegistry(), new ServiceCollection(), core).Add<EchoAction>(core);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_type_another_module_took_is_refused_and_the_message_names_both()
    {
        var registry = new ModuleWorkflowActionRegistry();
        var services = new ServiceCollection();
        For(registry, services, "A").Add<EchoAction>("A.Echo");
        var again = () => For(registry, services, "A").Add<EchoAction>("A.Echo");
        again.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("A.Echo");
    }
}
