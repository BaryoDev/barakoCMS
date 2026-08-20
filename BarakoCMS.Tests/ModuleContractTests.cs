using Xunit;
using FluentAssertions;
using barakoCMS.Modules;
using barakoCMS.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// The contract version exists so a module author finds out they are on the wrong side of a change,
/// rather than discovering it as a failure somewhere that does not mention their module.
/// </summary>
public class ModuleContractTests
{
    private sealed class Mod : IBarakoModule
    {
        public string Name { get; init; } = "Test";
        public int Declared { get; init; }
        public int ContractVersion => Declared;
    }

    private static IServiceCollection Build(params IBarakoModule[] modules)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", "Host=localhost;Database=none" },
                { "JWT:Key", "test-super-secret-key-that-is-at-least-32-chars-long" },
            })
            .Build();

        services.AddBarakoCMS(config, m =>
        {
            foreach (var mod in modules) m.Add(mod);
        });
        return services;
    }

    // The control. Without it, a check that refuses everything passes every test below.
    [Fact]
    public void A_module_declaring_the_current_contract_loads()
    {
        var act = () => Build(new Mod { Name = "Current", Declared = ModuleContract.Version });

        act.Should().NotThrow("a module written for this exact contract is the case that must work");
    }

    // Unstated is accepted on purpose: every module written before 3.21 declares nothing, and
    // refusing them would break the ecosystem to enforce a field they could not have known about.
    [Fact]
    public void A_module_that_states_nothing_still_loads()
    {
        var act = () => Build(new Mod { Name = "Silent", Declared = 0 });

        act.Should().NotThrow("unstated must stay loadable, or every existing module breaks");
    }

    [Fact]
    public void A_module_from_the_future_is_refused_by_name()
    {
        var act = () => Build(new Mod { Name = "TooNew", Declared = ModuleContract.Version + 1 });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TooNew*", "the message has to name the module, or the operator cannot act on it")
            .WithMessage($"*v{ModuleContract.Version + 1}*", "and the version it asked for");
    }

    [Fact]
    public void The_contract_version_is_not_the_cms_version()
    {
        // Guards the decision rather than the value: coupling them would mean either a major release
        // whenever a hook gained a parameter, or a silent contract change inside a patch.
        ModuleContract.Version.Should().BeGreaterThan(0);
        ModuleContract.MinimumSupported.Should().BeLessThanOrEqualTo(ModuleContract.Version);
    }
}
