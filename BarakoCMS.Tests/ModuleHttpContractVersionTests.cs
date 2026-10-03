using barakoCMS.Features.Monitoring.Describe;
using barakoCMS.Modules;
using BarakoCMS.Pages;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="IBarakoModule.HttpContractVersion"/>, the number a module gives the HTTP surface of
/// its own endpoints, and how it reaches the describe document.
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
    public void The_describe_document_reports_what_each_running_module_declared()
    {
        IBarakoModule stated = new Stated();
        IBarakoModule silent = new Silent();

        var described = DescribeDocument.Modules(ModuleCatalogue.Of([stated, silent], [stated, silent]));

        described.Should().HaveCount(2);
        described.Select(m => (m.Name, m.HttpContractVersion)).Should().Equal([("Silent", 0), ("Stated", 7)]);
    }

    [Fact]
    public void Pages_states_the_number_its_bodies_already_carry()
    {
        IBarakoModule pages = new PagesModule();

        PagesContract.Version.Should().NotBe(0, "zero is the unstated default, so equality below would prove nothing");
        pages.HttpContractVersion.Should().Be(PagesContract.Version,
            "the contract field in each Pages body and the number in the describe document are one number, not two to keep in step");
    }

    /// <summary>
    /// The three modules whose routes a renderer or a site calls today. Zero would leave a reader
    /// unable to tell "serves nothing" from "not versioned". Files is at 2 since a private file
    /// another user uploaded opens to a capability rather than to a role name (#886).
    /// </summary>
    [Fact]
    public void Forms_Files_and_AI_each_state_their_version()
    {
        IBarakoModule[] modules =
        [
            new BarakoCMS.Forms.FormsModule(),
            new BarakoCMS.Files.FilesModule(),
            new BarakoCMS.AI.AiModule(),
        ];

        modules.Should().HaveCount(3);
        modules.Select(m => (m.Name, m.HttpContractVersion)).Should().Equal([("Forms", 1), ("Files", 2), ("AI", 1)]);
    }
}
