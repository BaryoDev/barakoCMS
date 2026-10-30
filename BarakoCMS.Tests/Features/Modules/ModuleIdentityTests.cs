#pragma warning disable CS0618 // Accounting is obsolete in 5.5 and is removed in #1285
using barakoCMS.Modules;
using BarakoCMS.Accounting;
using BarakoCMS.FeatureFlags;
using BarakoCMS.Files;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Modules;

/// <summary>
/// The identity a module states about itself (package, category, description, glyph) and how the
/// catalogue carries it to <c>GET /api/modules</c>.
/// </summary>
public class ModuleIdentityTests
{
    private sealed class SilentModule : IBarakoModule
    {
        public string Name => "Silent";
    }

    private sealed class MisfiledModule : IBarakoModule
    {
        public string Name => "Misfiled";
        public string? Category => "Gadgets";
    }

    private static IBarakoModule[] FirstParty() => [new FilesModule(), new AccountingModule(), new FeatureFlagsModule()];

    [Fact]
    public void A_module_that_declares_nothing_reports_no_identity()
    {
        var entry = ModuleCatalogue.Of([new SilentModule()], [new SilentModule()]).Entries.Single();

        entry.PackageId.Should().BeNull();
        entry.PackageVersion.Should().BeNull();
        entry.Category.Should().BeNull();
        entry.Description.Should().BeNull();
        entry.Glyph.Should().BeNull();
    }

    [Fact]
    public void A_first_party_module_reports_its_package_category_description_and_glyph()
    {
        var modules = FirstParty();
        var entries = ModuleCatalogue.Of(modules, modules).Entries;

        entries.Should().HaveCount(3);
        foreach (var entry in entries)
        {
            entry.PackageId.Should().StartWith("BarakoCMS.");
            entry.PackageVersion.Should().NotBeNullOrWhiteSpace();
            entry.Description.Should().NotBeNullOrWhiteSpace();
            entry.Glyph.Should().NotBeNullOrWhiteSpace();
            ModuleCategory.All.Should().Contain(entry.Category!);
        }

        entries.Single(e => e.Name == "Files").PackageId.Should().Be("BarakoCMS.Files");
        entries.Single(e => e.Name == "Accounting").Category.Should().Be(ModuleCategory.Business);
    }

    [Fact]
    public void A_category_outside_the_closed_set_is_reported_as_null()
    {
        var misfiled = new MisfiledModule();

        ModuleCatalogue.Of([misfiled], [misfiled]).Entries.Single().Category.Should().BeNull();
        ModuleCategory.All.Should().Equal("Content", "Comms", "Access", "Platform", "Insight", "Business");
    }

    [Fact]
    public void The_package_version_has_no_build_metadata()
    {
        var version = ModuleMetadata.VersionOf(typeof(FilesModule));

        version.Should().NotBeNullOrWhiteSpace();
        version.Should().NotContain("+");
    }
}
