using barakoCMS.Modules;
using BarakoCMS.Pages;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="IBarakoModule.HttpContractVersion"/>, the number a module gives the HTTP surface of
/// its own endpoints, and how it reaches the catalogue <c>GET /api/meta</c> reads.
/// </summary>
public class ModuleHttpContractVersionTests
{
    private sealed class Silent : IBarakoModule
    {
        public string Name => "Silent";
    }

    // The two numbers differ from each other and from the default, so reading the wrong member
    // cannot produce the right answer.
    private sealed class Stated : IBarakoModule
    {
        public string Name => "Stated";

        public int ContractVersion => ModuleContract.Version;

        public int HttpContractVersion => 7;
    }

    [Fact]
    public void A_module_that_states_no_http_version_reports_zero()
    {
        IBarakoModule module = new Silent();

        module.HttpContractVersion.Should().Be(0, "a module written before the member existed still compiles and says nothing");
    }

    [Fact]
    public void The_catalogue_carries_the_http_version_a_module_declared()
    {
        IBarakoModule stated = new Stated();
        IBarakoModule silent = new Silent();

        var catalogue = ModuleCatalogue.Of([stated, silent], [stated]);

        catalogue.Entries.Should().HaveCount(2);
        catalogue.Entries[0].Name.Should().Be("Stated");
        catalogue.Entries[0].HttpContractVersion.Should().Be(7);
        catalogue.Entries[0].ContractVersion.Should().Be(ModuleContract.Version, "the module contract number is a different one and stays where it was");
        catalogue.Entries[1].HttpContractVersion.Should().Be(0);
    }

    [Fact]
    public void Pages_states_the_number_its_bodies_already_carry()
    {
        IBarakoModule pages = new PagesModule();

        PagesContract.Version.Should().NotBe(0, "zero is the unstated default, so equality below would prove nothing");
        pages.HttpContractVersion.Should().Be(PagesContract.Version,
            "the contract field in each Pages body and the number in /api/meta are one number, not two to keep in step");
    }
}
