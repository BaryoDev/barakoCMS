// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using System.Reflection;
using barakoCMS.Extensions;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What a module assembly that cannot load does to a host, and what a library that is not a module
/// does (#1010). <c>Barako.Fixture.Broken</c> defines a module beside a type no barakoCMS has;
/// <c>Barako.Fixture.Library</c> has the same broken type and no module. Both are built by
/// <c>Fixtures/LegacyModules/build.sh</c>.
/// </summary>
public class ModuleLoadFailureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyModules");

    // Loading it is safe for every other host in this process: it defines a module, so the
    // endpoint scan skips it.
    private static readonly Assembly Broken = Assembly.LoadFrom(Path.Combine(FixtureDirectory, "Barako.Fixture.Broken.dll"));

    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var pairs = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = IntegrationTestFixture.JwtKey,
        };
        foreach (var (key, value) in settings) pairs[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
    }

    [Fact]
    public void Enabling_a_module_whose_assembly_cannot_load_names_the_assembly_and_the_missing_type()
    {
        var sink = new LegacyModuleHostTests.CollectingSink();
        var services = new ServiceCollection();

        Action act;
        using (sink.Installed())
        {
            act = () => services.AddBarakoCMS(
                Config(("BarakoCMS:Modules:Enabled", "BrokenFixture")),
                modules =>
                {
                    modules.Discover = false;
                    modules.DiscoverFrom(Broken);
                });

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*'BrokenFixture'*")
                .WithMessage("*Barako.Fixture.Broken*barakoCMS.Models.NeverShipped*");
        }

        sink.Events.Where(e => e.Level == LogEventLevel.Warning)
            .Select(e => e.RenderMessage())
            .Should().Contain(m => m.Contains("Barako.Fixture.Broken") && m.Contains("barakoCMS.Models.NeverShipped"),
                "discovery warns when it skips the assembly, before anything can throw");
    }

    [Fact]
    public void An_assembly_discovery_skips_is_warned_about_once()
    {
        var sink = new LegacyModuleHostTests.CollectingSink();
        var services = new ServiceCollection();

        using (sink.Installed())
        {
            services.AddBarakoCMS(Config(), modules =>
            {
                modules.Discover = false;
                modules.DiscoverFrom(Broken);
            });
        }

        sink.Events.Where(e => e.Level == LogEventLevel.Warning)
            .Select(e => e.RenderMessage())
            .Where(m => m.Contains("Barako.Fixture.Broken"))
            .Should().ContainSingle("discovery warns, and the endpoint scan leaves out what discovery skipped without warning again");
    }

    [Fact]
    public void Adding_a_module_whose_assembly_cannot_load_is_refused()
    {
        var module = (IBarakoModule)Activator.CreateInstance(
            Broken.GetType("Barako.Fixture.Broken.BrokenFixtureModule", throwOnError: true)!)!;

        var act = () => new BarakoModuleBuilder().Add(module);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Barako.Fixture.Broken*barakoCMS.Models.NeverShipped*",
                "the endpoint scan leaves the assembly out, so the module would run with none of its endpoints");
    }

    /// <summary>
    /// The library is never loaded into this process (the build copies it under another extension so
    /// nothing loads it by accident): every later host would scan it and fail, which is the point.
    /// A stand-in that fails <c>GetTypes</c> the way it would, with the
    /// library's file as its location, is enough for the decision.
    /// </summary>
    [Fact]
    public void A_library_that_defines_no_module_is_still_scanned_so_its_load_failure_surfaces()
    {
        var library = new UnloadableAssembly(Path.Combine(FixtureDirectory, "Barako.Fixture.Library.metadata"), "Barako.Fixture.Library");

        Host.LoadsOrIsSkipped(library).Should().BeTrue(
            "a host's own library is not a module to leave out; FastEndpoints scans it and fails as before");
    }

    [Fact]
    public void A_module_assembly_whose_module_type_itself_cannot_load_is_still_recognised_and_skipped()
    {
        // The control for the test above: the same stand-in over a file that defines a module.
        var module = new UnloadableAssembly(Path.Combine(FixtureDirectory, "Barako.Fixture.Broken.dll"), "Barako.Fixture.Broken.Standin");

        Host.LoadsOrIsSkipped(module).Should().BeFalse();
    }

    private sealed class UnloadableAssembly(string location, string name) : Assembly
    {
        public override string Location => location;

        public override string FullName => GetName().FullName;

        public override bool IsDynamic => false;

        public override AssemblyName GetName() => new(name) { Version = new Version(1, 0, 0, 0) };

        public override AssemblyName GetName(bool copiedName) => GetName();

        public override AssemblyName[] GetReferencedAssemblies() => [new AssemblyName("barakoCMS")];

        // Nothing loads, the way a module whose own module type is missing fails.
        public override Type[] GetTypes() => throw new ReflectionTypeLoadException(
            [null], [new TypeLoadException("Could not load type 'barakoCMS.Models.NeverShipped' from assembly 'barakoCMS'.")]);
    }
}
