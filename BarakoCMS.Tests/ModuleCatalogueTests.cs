using barakoCMS.Modules;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A module cannot declare a settings section the host serves itself, since the fixed route wins and
/// the section would never be reached.
/// </summary>
public class ModuleCatalogueTests
{
    private sealed class SectionModule(string section) : IBarakoModule
    {
        public string Name => "Section.Stand.In";

        public IReadOnlyList<ModuleSettingsSection> SettingsSections =>
            [new ModuleSettingsSection(section, "A stand-in.", [new ModuleSettingsKey("Key", "A key.")])];
    }

    [Theory]
    [InlineData("email")]
    [InlineData("cdn-purge")]
    [InlineData("CDN-Purge")]
    public void A_module_section_named_like_a_host_route_fails_at_start(string section)
    {
        IBarakoModule[] modules = [new SectionModule(section)];

        var start = () => ModuleCatalogue.Of(modules, modules);

        start.Should().Throw<InvalidOperationException>().WithMessage($"*'{section}' is reserved*");
    }

    [Fact]
    public void A_module_section_with_another_name_starts()
    {
        IBarakoModule[] modules = [new SectionModule("Cdn.Other")];

        ModuleCatalogue.Of(modules, modules).Entries.Should().ContainSingle();
    }
}
