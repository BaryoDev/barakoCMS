using System.Reflection;
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
/// </summary>
public class PublicSurfaceTests
{
    private static readonly Assembly Core = typeof(barakoCMS.Modules.IBarakoModule).Assembly;

    // The two documented extension points. A module author implements IWorkflowAction and the engine
    // is resolvable, so both are contract rather than accident.
    private static readonly string[] Allowed =
    [
        "barakoCMS.Features.Workflows.IWorkflowAction",
        "barakoCMS.Features.Workflows.IWorkflowEngine",
    ];

    [Fact]
    public void No_feature_slice_is_public_beyond_the_two_documented_extension_points()
    {
        var exported = Core.GetExportedTypes()
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
    public void The_two_extension_points_are_actually_public()
    {
        var exported = Core.GetExportedTypes().Select(t => t.FullName).ToArray();

        exported.Should().Contain(Allowed[0], "a module author implements this, so it has to be reachable");
        exported.Should().Contain(Allowed[1], "and resolve the engine that runs it");
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
}
