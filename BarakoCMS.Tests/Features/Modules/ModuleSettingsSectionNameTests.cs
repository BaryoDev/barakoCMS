using barakoCMS.Modules;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Modules;

/// <summary>
/// Section names are the route segment of <c>/api/tenant-settings/{section}</c>, so the catalogue
/// refuses two modules that claim one name and a name the host already serves (#896).
/// </summary>
public class ModuleSettingsSectionNameTests
{
    private sealed class SectionModule(string name, params string[] sections) : IBarakoModule
    {
        public string Name => name;

        public IReadOnlyList<ModuleSettingsSection> SettingsSections { get; } =
            sections.Select(s => new ModuleSettingsSection(s, "A section", [new ModuleSettingsKey("Key", "A key", "General")])).ToArray();
    }

    [Fact]
    public void Two_modules_with_one_section_name_are_refused_whatever_the_case()
    {
        IBarakoModule[] modules = [new SectionModule("First", "Shared.Section"), new SectionModule("Second", "shared.section")];

        var act = () => ModuleCatalogue.Of(modules, modules);

        act.Should().Throw<InvalidOperationException>().WithMessage("*First*Second*");
    }

    [Theory]
    [InlineData("email")]
    [InlineData("Email")]
    [InlineData("EMAIL")]
    public void The_section_name_email_is_reserved_for_the_host_route(string name)
    {
        IBarakoModule[] modules = [new SectionModule("Mailer", name)];

        var act = () => ModuleCatalogue.Of(modules, modules);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mailer*reserved*");
    }

    [Fact]
    public void A_switched_off_module_does_not_stop_the_host_from_starting()
    {
        IBarakoModule[] seen = [new SectionModule("Off", "email"), new SectionModule("On", "Fine")];

        var catalogue = ModuleCatalogue.Of(seen, [seen[1]]);

        catalogue.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Distinct_names_are_accepted()
    {
        IBarakoModule[] modules = [new SectionModule("First", "One"), new SectionModule("Second", "Two", "Three")];

        ModuleCatalogue.Of(modules, modules).Entries.Should().HaveCount(2);
    }
}
