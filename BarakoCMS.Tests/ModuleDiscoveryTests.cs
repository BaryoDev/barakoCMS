// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Reflection;
using barakoCMS.Modules;
using BarakoCMS.Files;
using BarakoCMS.Files.S3;
using ContractOnlyProbe;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
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

    /// <summary>
    /// Only a library whose dependency closure reaches core is loaded. Over a synthetic context,
    /// because the real one loads whatever it holds and a wrong filter would still pass every test
    /// above: everything in this process loads fine.
    /// </summary>
    [Fact]
    public void Only_a_library_whose_dependencies_reach_core_is_a_candidate()
    {
        var context = new DependencyContext(
            new TargetInfo(".NETCoreApp,Version=v10.0", null, null, isPortable: true),
            CompilationOptions.Default,
            compileLibraries: [],
            runtimeLibraries:
            [
                Library("BarakoCMS.Abstractions"),
                Library("BarakoCMS", "BarakoCMS.Abstractions"),
                Library("Acme.Crm", "BarakoCMS"),
                Library("Acme.Crm.Extras", "Acme.Crm"),
                Library("Acme.ContractOnly", "BarakoCMS.Abstractions"),
                Library("Newtonsoft.Json"),
                Library("Unrelated", "Newtonsoft.Json"),
            ],
            runtimeGraph: []);

        var reaches = BarakoModuleBuilder.ReachesCore(context);

        reaches("Acme.Crm").Should().BeTrue("a direct reference to core");
        reaches("Acme.Crm.Extras").Should().BeTrue("reach is transitive, the way Files.S3 sits on Files");
        reaches("Unrelated").Should().BeFalse("an unrelated package is never loaded on the chance it holds a module");
        reaches("BarakoCMS").Should().BeFalse("core does not depend on itself, and depending on the "
          + "contract package is not depending on core. Reading the core's name off the assembly "
          + "holding IBarakoModule makes this true, and core is then scanned for modules");
    }

    /// <summary>
    /// A module built on the contract package alone never compiles against core, so its
    /// dependency closure holds no core entry. It is still a candidate.
    /// </summary>
    [Fact]
    public void A_library_that_references_only_the_contract_package_is_a_candidate()
    {
        var context = new DependencyContext(
            new TargetInfo(".NETCoreApp,Version=v10.0", null, null, isPortable: true),
            CompilationOptions.Default,
            compileLibraries: [],
            runtimeLibraries:
            [
                Library("BarakoCMS.Abstractions"),
                Library("BarakoCMS", "BarakoCMS.Abstractions"),
                Library("Acme.ContractOnly", "BarakoCMS.Abstractions"),
                Library("Acme.ContractOnly.Extras", "Acme.ContractOnly"),
                Library("Unrelated"),
            ],
            runtimeGraph: []);

        var reaches = BarakoModuleBuilder.ReachesCore(context);

        reaches("Acme.ContractOnly").Should().BeTrue("a direct reference to the contract package");
        reaches("Acme.ContractOnly.Extras").Should().BeTrue("reach is transitive");
        reaches("Unrelated").Should().BeFalse();
        reaches("BarakoCMS").Should().BeFalse("core references the contract package and is not scanned for modules");    }

    private static RuntimeLibrary Library(string name, params string[] dependsOn) =>
        new("package", name, "1.0.0", hash: string.Empty,
            runtimeAssemblyGroups: [], nativeLibraryGroups: [], resourceAssemblies: [],
            dependencies: dependsOn.Select(d => new Dependency(d, "1.0.0")).ToList(),
            serviceable: false);

    // The assemblies the fake contexts below hand back. Marked is a library built on the contract
    // alone that carries the attribute; Unmarked is this test assembly, which holds
    // DiscoverableProbeModule and carries none.
    private static readonly Assembly Marked = typeof(ContractOnlyProbeModule).Assembly;
    private static readonly Assembly Unmarked = typeof(ModuleDiscoveryTests).Assembly;

    private static RuntimeLibrary LibraryWithAssembly(string name, params string[] dependsOn) =>
        new("package", name, "1.0.0", hash: string.Empty,
            runtimeAssemblyGroups: [new RuntimeAssetGroup(string.Empty, [$"lib/net10.0/{name}.dll"])],
            nativeLibraryGroups: [], resourceAssemblies: [],
            dependencies: dependsOn.Select(d => new Dependency(d, "1.0.0")).ToList(),
            serviceable: false);

    /// <summary>
    /// Runs discovery over a synthetic context holding <paramref name="libraries"/> and the two
    /// host libraries, and answers what it loaded. <paramref name="assemblies"/> says what each
    /// library name loads as.
    /// </summary>
    private static (BarakoModuleBuilder Builder, List<string> Loaded) Discover(
        IReadOnlyDictionary<string, Assembly> assemblies, params RuntimeLibrary[] libraries)
    {
        var context = new DependencyContext(
            new TargetInfo(".NETCoreApp,Version=v10.0", null, null, isPortable: true),
            CompilationOptions.Default,
            compileLibraries: [],
            runtimeLibraries:
            [
                Library("BarakoCMS.Abstractions"),
                Library("BarakoCMS", "BarakoCMS.Abstractions"),
                .. libraries,
            ],
            runtimeGraph: []);
        var loaded = new List<string>();
        var builder = new BarakoModuleBuilder();

        builder.DiscoverFrom(context, name =>
        {
            loaded.Add(name.Name!);
            return assemblies[name.Name!];
        });

        return (builder, loaded);
    }

    [Fact]
    public void A_contract_only_library_with_the_attribute_is_scanned_without_a_warning()
    {
        // The control: the probe really is a library that never references core.
        Marked.GetReferencedAssemblies().Select(a => a.Name).Should().NotContain("barakoCMS");
        Marked.IsDefined(typeof(BarakoModuleAssemblyAttribute), inherit: false).Should().BeTrue();

        var (builder, loaded) = Discover(
            new Dictionary<string, Assembly> { ["Acme.Marked"] = Marked },
            LibraryWithAssembly("Acme.Marked", "BarakoCMS.Abstractions"));

        loaded.Should().Equal("Acme.Marked");
        builder.Modules.Should().ContainSingle(m => m is ContractOnlyProbeModule);
        builder.Unmarked.Should().BeEmpty("it carries the attribute, so there is nothing to warn about");
    }

    [Fact]
    public void A_contract_only_library_without_the_attribute_is_still_scanned_and_listed_for_one_warning()
    {
        var (builder, _) = Discover(
            new Dictionary<string, Assembly> { ["Acme.Old"] = Unmarked },
            LibraryWithAssembly("Acme.Old", "BarakoCMS.Abstractions"));

        builder.Modules.Should().Contain(m => m is DiscoverableProbeModule, "5.5.0 to 5.8.0 scanned it, so 5.9.0 does too");
        builder.Unmarked.Should().ContainSingle().Which.Should().BeSameAs(Unmarked);
    }

    [Fact]
    public void A_contract_only_library_that_holds_no_module_is_not_warned_about()
    {
        var (builder, _) = Discover(
            new Dictionary<string, Assembly> { ["Acme.Helpers"] = typeof(IBarakoModule).Assembly },
            LibraryWithAssembly("Acme.Helpers", "BarakoCMS.Abstractions"));

        builder.Unmarked.Should().BeEmpty("a warning naming a library with no module would be noise");
    }

    [Fact]
    public void A_library_that_reaches_core_is_scanned_without_the_attribute_and_without_a_warning()
    {
        var (builder, _) = Discover(
            new Dictionary<string, Assembly> { ["Acme.Crm"] = Unmarked },
            LibraryWithAssembly("Acme.Crm", "BarakoCMS"));

        builder.Modules.Should().Contain(m => m is DiscoverableProbeModule);
        builder.Unmarked.Should().BeEmpty("a library that reaches core is scanned as it always was");
    }

    [Fact]
    public void An_unrelated_library_is_never_loaded()
    {
        var (builder, loaded) = Discover(
            new Dictionary<string, Assembly> { ["Acme.Crm"] = Unmarked },
            LibraryWithAssembly("Acme.Crm", "BarakoCMS"),
            LibraryWithAssembly("Newtonsoft.Json"),
            LibraryWithAssembly("Unrelated", "Newtonsoft.Json"));

        loaded.Should().Equal("Acme.Crm");
        builder.Modules.Should().NotBeEmpty("an empty result would make the assertion above vacuous");
    }

    [Fact]
    public void The_host_warns_once_per_unmarked_library_and_says_how_to_fix_it()
    {
        var sink = new CollectingSink();
        using (sink.Installed())
        {
            Host.WarnUnmarked([Unmarked]);
        }

        var warning = sink.Events.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogEventLevel.Warning);
        var message = warning.RenderMessage();
        message.Should().Contain(Unmarked.GetName().Name!);
        message.Should().Contain("BarakoModuleAssembly");
        message.Should().Contain("6.0.0");
    }

    [Fact]
    public void Host_warns_about_nothing_when_no_library_is_unmarked()
    {
        var sink = new CollectingSink();
        using (sink.Installed())
        {
            Host.WarnUnmarked([]);
        }

        sink.Events.Should().BeEmpty();
    }

    /// <summary>
    /// Every first-party module is found by the attribute, so none of them depends on the
    /// contract-only fallback that 6.0.0 removes.
    /// </summary>
    [Fact]
    public void Every_first_party_module_assembly_carries_the_attribute()
    {
        var modules = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith("BarakoCMS.", StringComparison.Ordinal) == true)
            .Where(a => a.GetTypes().Any(t => t is { IsAbstract: false, IsInterface: false }
                && typeof(IBarakoModule).IsAssignableFrom(t) && t.IsPublic))
            // BarakoCMS.Testing is not a module package: it hosts a real core for tests and its one
            // module is the fixture, which reaches core and so is scanned without the attribute.
            .Where(a => a.GetName().Name is not ("BarakoCMS.Tests" or "BarakoCMS.Abstractions" or "BarakoCMS.Testing"))
            .ToList();

        modules.Should().HaveCountGreaterThan(8, "the suite references every first-party module");
        modules.Where(a => !a.IsDefined(typeof(BarakoModuleAssemblyAttribute), inherit: false))
            .Select(a => a.GetName().Name)
            .Should().BeEmpty("a module without the attribute is warned about at startup and not scanned from 6.0.0");
    }

    /// <summary>Warnings written while <see cref="Installed"/> is open, from this test's async flow only.</summary>
    private sealed class CollectingSink : ILogEventSink
    {
        private const string Marker = "DiscoveryTest";
        private readonly string _id = Guid.NewGuid().ToString("N");
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events => _events;

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue(Marker, out var value)
                && value is ScalarValue { Value: string id } && id == _id)
            {
                lock (_events)
                {
                    _events.Add(logEvent);
                }
            }
        }

        public IDisposable Installed()
        {
            var previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().Enrich.FromLogContext().WriteTo.Sink(this).CreateLogger();
            return new Restore(previous, LogContext.PushProperty(Marker, _id));
        }

        private sealed class Restore(Serilog.ILogger previous, IDisposable scope) : IDisposable
        {
            public void Dispose()
            {
                scope.Dispose();
                Log.Logger = previous;
            }
        }
    }

    [Fact]
    public void AddBarakoCMS_discovers_by_default()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services.AsDevelopmentHost(), Config(), configureModules: null);

        RegisteredTypes(services).Should().Contain(typeof(DiscoverableProbeModule));
    }

    [Fact]
    public void The_builder_opt_out_keeps_only_the_explicit_list()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services.AsDevelopmentHost(), Config(), m =>
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

        Host.AddBarakoCMS(services.AsDevelopmentHost(), Config(("BarakoCMS:Modules:Discover", "false")), configureModules: null);

        RegisteredTypes(services).Should().BeEmpty();
    }

    [Fact]
    public void What_the_callback_sets_wins_over_the_configuration_key()
    {
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services.AsDevelopmentHost(), Config(("BarakoCMS:Modules:Discover", "false")), m => m.Discover = true);

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

        Host.AddBarakoCMS(services.AsDevelopmentHost(), Config(("BarakoCMS:Modules:Enabled", "Files")), configureModules: null);

        RegisteredTypes(services).Should().Equal(typeof(FilesModule));
        var catalogue = (ModuleCatalogue)services.Single(d => d.ServiceType == typeof(ModuleCatalogue)).ImplementationInstance!;
        catalogue.Entries.Should().Contain(e => e.Name == DiscoverableProbeModule.ModuleName && !e.Enabled);
        catalogue.Entries.Should().Contain(e => e.Name == "Files" && e.Enabled);
    }
}
