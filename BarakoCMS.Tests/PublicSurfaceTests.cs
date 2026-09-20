using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// CLAUDE.md section 6 defines the package's public surface as a boundary rather than as whatever
/// happens to be marked public, and puts everything under Features/ outside it: the endpoints, their
/// request and response records and their validators are how this host implements the API, not
/// something another assembly compiles against.
///
/// That boundary was established by widening nothing and narrowing a lot, and the compiler cannot
/// hold it: adding an accessibility keyword is legal, silent, and permanent once released, because
/// a type that ships public cannot be made internal again inside a major version.
///
/// The contract now lives in its own assembly, so the scan covers both: a public Features type is
/// out of scope wherever it is declared.
/// </summary>
public class PublicSurfaceTests
{
    private static readonly Assembly Core = typeof(barakoCMS.Data.DataSeeder).Assembly;
    private static readonly Assembly Contract = typeof(barakoCMS.Modules.IBarakoModule).Assembly;

    private static readonly Assembly[] Both = [Core, Contract];

    // The documented extension points. A module author implements IWorkflowAction and the engine is
    // resolvable, so both are contract rather than accident. WorkflowActionResult is the outcome an
    // action returns, so it is reachable by necessity once IWorkflowAction is.
    private static readonly string[] Allowed =
    [
        "barakoCMS.Features.Workflows.IWorkflowAction",
        "barakoCMS.Features.Workflows.IWorkflowEngine",
        "barakoCMS.Features.Workflows.WorkflowActionResult",
    ];

    // Without this the two fields can silently become one assembly again, and every assertion below
    // would then be checking half of what it claims to check.
    [Fact]
    public void The_contract_and_the_core_are_separate_assemblies()
    {
        Contract.Should().NotBeSameAs(Core,
            "the contract ships as BarakoCMS.Abstractions so a module can compile against it without "
          + "the host. If these are one assembly again the split was undone");

        Contract.GetName().Name.Should().Be("BarakoCMS.Abstractions");
        Core.GetName().Name.Should().Be("barakoCMS");
    }

    [Fact]
    public void No_feature_slice_is_public_beyond_the_two_documented_extension_points()
    {
        var exported = Both
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => t.FullName is not null && t.FullName.StartsWith("barakoCMS.Features.", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Where(name => !Allowed.Contains(name))
            .OrderBy(name => name)
            .ToArray();

        exported.Should().BeEmpty(
            "everything under Features/ is out of scope for the section 6 stability rule, so a public "
          + "one there is a contract nobody agreed to and cannot be withdrawn until the next major. "
          + "If the type is genuinely an extension point, add it to Allowed here and to section 6, "
          + "which makes the widening a reviewable line in the diff rather than a keyword");
    }

    // The control. Without it a typo in the namespace filter finds nothing and the assertion above
    // passes on an empty set, which is the shape of gate this project has been bitten by repeatedly.
    [Fact]
    public void The_documented_extension_points_are_actually_public()
    {
        var exported = Both.SelectMany(a => a.GetExportedTypes()).Select(t => t.FullName).ToArray();

        exported.Should().NotBeEmpty("both assemblies export types, so an empty scan is a broken scan");
        exported.Should().Contain(Allowed[0], "a module author implements this, so it has to be reachable");
        exported.Should().Contain(Allowed[1], "and resolve the engine that runs it");
        exported.Should().Contain(Allowed[2], "and return the outcome type the interface is declared in terms of");
    }

    // FastEndpoints discovers internal endpoint classes, and InternalsVisibleTo covers the tests, so
    // the narrowing costs nothing at runtime. Asserted rather than assumed, because if discovery ever
    // stopped finding internal endpoints the fix would look like "make them public again".
    [Fact]
    public void Feature_endpoints_still_exist_as_internal_types()
    {
        var endpoints = Core.GetTypes()
            .Count(t => t.FullName is not null
                     && t.FullName.StartsWith("barakoCMS.Features.", StringComparison.Ordinal)
                     && t.Name == "Endpoint");

        endpoints.Should().BeGreaterThan(20,
            "the slices are still there and still internal. A small number here means either the "
          + "endpoints moved or they were made public, and both change what the assertion above proves");
    }

    /// <summary>
    /// The contract compiles without the host. That is the whole reason it is a separate assembly:
    /// a reference back to the core would make every "is this contract" question a review question
    /// again, and nothing else in the build would notice.
    /// </summary>
    [Fact]
    public void The_contract_assembly_does_not_reference_the_core()
    {
        var referenced = Contract.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        referenced.Should().NotBeEmpty(
            "the contract references the framework and Marten at least, so an empty list means the "
          + "reflection read nothing and the assertion below would hold vacuously");

        referenced.Should().NotContain("barakoCMS",
            "BarakoCMS.Abstractions is what a module compiles against. Referencing the core would "
          + "pull the host back in and put the whole application inside the package contract again");
    }

    /// <summary>
    /// The same rule read from the project file, because the assertion above cannot see a reference
    /// nothing uses yet: the compiler drops an unused one from the assembly's reference list, so a
    /// ProjectReference added today and used tomorrow would land green today.
    /// </summary>
    [Fact]
    public void The_contract_project_references_neither_the_core_nor_a_module()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull("the test must be able to find the repository root");

        var csproj = Path.Combine(root!.FullName, "BarakoCMS.Abstractions", "BarakoCMS.Abstractions.csproj");
        File.Exists(csproj).Should().BeTrue("the contract project is where the package surface lives");

        var references = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        references.Should().BeEmpty(
            "the contract is the bottom of the graph. Every other project in this repository "
          + "references it, so any reference out of it is either a cycle or a module leaking into "
          + "the package surface");
    }
}
