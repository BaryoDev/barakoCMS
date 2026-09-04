// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Modules;
using BarakoCMS.Files;
using BarakoCMS.Files.S3;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>BarakoModuleBuilder.DiscoverFrom()</c> with no arguments, from issue #172: modules are found
/// in the dependency context, so referencing a package is the whole install.
/// </summary>
/// <remarks>
/// This test process's dependency context is the one under test. It holds every first-party module
/// project this suite references and <see cref="DiscoverableProbeModule"/>, which is what a
/// third-party module looks like from here: a public type in an assembly that depends on core.
/// </remarks>
public class ModuleDiscoveryTests
{
    private sealed class Explicit : IBarakoModule
    {
        public string Name => "Explicit";
    }

    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        };
        foreach (var (key, value) in settings) pairs[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    private static IReadOnlyList<Type> RegisteredTypes(IServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(IBarakoModule))
            .Select(d => d.ImplementationInstance!.GetType())
            .ToList();

    [Fact]
    public void It_finds_a_public_module_type_in_an_assembly_that_depends_on_core()
    {
        var builder = new BarakoModuleBuilder();

        builder.DiscoverFrom();

        builder.Modules.Should().Contain(m => m is DiscoverableProbeModule, "this assembly depends on core and holds one");
        builder.Modules.Should().Contain(m => m is FilesModule, "and so does a first-party package");
    }

    [Fact]
    public void It_skips_a_module_the_host_already_added_and_keeps_the_explicit_instance()
    {
        var builder = new BarakoModuleBuilder();
        var added = new FilesModule();
        builder.Add(added);

        var act = () => builder.DiscoverFrom();

        act.Should().NotThrow("discovery is a sweep, and finding what the host added is expected");
        builder.Modules.Should().HaveCountGreaterThan(1, "the sweep found the others");
        builder.Modules.Where(m => m is FilesModule).Should().ContainSingle()
            .Which.Should().BeSameAs(added, "the explicit instance is the configured one");
        builder.Modules.Select(m => m.GetType()).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Assembly scan order is not something to rely on, so discovered modules are sorted by type
    /// name and a build is reproducible. Modules the host added keep their place ahead of them.
    /// </summary>
    [Fact]
    public void Discovered_modules_come_out_ordered_by_type_name_after_the_explicit_ones()
    {
        var builder = new BarakoModuleBuilder();
        // Sorts near the end alphabetically, so an unsorted sweep would not leave it first.
        builder.Add(new S3FilesModule());

        builder.DiscoverFrom();

        builder.Modules[0].Should().BeOfType<S3FilesModule>("what the host added comes first");
        var discovered = builder.Modules.Skip(1).Select(m => m.GetType().FullName!).ToList();
        discovered.Should().HaveCountGreaterThan(2, "an ordering assertion over one item proves nothing");
        discovered.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    /// <summary>
    /// The private nested doubles this project is full of are not modules anyone ships, and
    /// constructing them would make every test that calls AddBarakoCMS run a dozen of them.
    /// </summary>
    [Fact]
    public void It_ignores_private_nested_module_types()
    {
        // The control: this assembly does hold such types, so the filter has something to exclude.
        typeof(ModuleDiscoveryTests).Assembly.GetTypes().Should().Contain(
            t => t.IsNested && !t.IsAbstract && typeof(IBarakoModule).IsAssignableFrom(t),
            "the test doubles are what the filter is for");

        var builder = new BarakoModuleBuilder();
        builder.DiscoverFrom();

        builder.Modules.Should().NotBeEmpty();
        builder.Modules.Select(m => m.GetType()).Should().OnlyContain(t => t.IsPublic && !t.IsNested);
    }

    [Fact]
    public void AddBarakoCMS_discovers_by_default()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, Config(), configureModules: null);

        RegisteredTypes(services).Should().Contain(typeof(DiscoverableProbeModule));
    }

    [Fact]
    public void The_builder_opt_out_keeps_only_the_explicit_list()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, Config(), m =>
        {
            m.Discover = false;
            m.Add(new Explicit());
        });

        RegisteredTypes(services).Should().Equal(typeof(Explicit));
    }

    [Fact]
    public void The_configuration_key_turns_discovery_off_for_a_host_with_no_callback()
    {
        // Program.cs in core calls AddBarakoCMS(configuration) with no callback, so an operator or
        // a test host needs a way to say it without code. DiscoveryDefault relies on this, through
        // the environment variable form of the same key.
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, Config(("BarakoCMS:Modules:Discover", "false")), configureModules: null);

        RegisteredTypes(services).Should().BeEmpty();
    }

    [Fact]
    public void What_the_callback_sets_wins_over_the_configuration_key()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, Config(("BarakoCMS:Modules:Discover", "false")), m => m.Discover = true);

        RegisteredTypes(services).Should().Contain(typeof(DiscoverableProbeModule));
    }

    /// <summary>
    /// The two halves together: discovery finds a module and the enabled list decides whether it
    /// runs. The catalogue still records it, so the endpoint can say "installed, off".
    /// </summary>
    [Fact]
    public void A_discovered_module_left_off_the_enabled_list_is_seen_but_not_registered()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, Config(("BarakoCMS:Modules:Enabled", "Files")), configureModules: null);

        RegisteredTypes(services).Should().Equal(typeof(FilesModule));
        var catalogue = (ModuleCatalogue)services.Single(d => d.ServiceType == typeof(ModuleCatalogue)).ImplementationInstance!;
        catalogue.Entries.Should().Contain(e => e.Name == DiscoverableProbeModule.ModuleName && !e.Enabled);
        catalogue.Entries.Should().Contain(e => e.Name == "Files" && e.Enabled);
    }
}
