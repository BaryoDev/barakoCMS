using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A module compiled against barakoCMS 4.0 to 4.2 names every barakoCMS type by assembly
/// <c>barakoCMS</c>. 4.3.0 moved a hundred of them into BarakoCMS.Abstractions, so each one has to
/// still resolve through that name, or the module fails to load (#1010). The list covers every 4.x
/// release through 4.4.0, so a later move without a forwarder fails here too.
/// </summary>
public class TypeForwardingTests
{
    private static readonly string[] PublishedTypes = File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "LegacyModules", "barakoCMS-4.x-public-types.txt"))
        .Where(line => line.Length > 0)
        .ToArray();

    [Fact]
    public void Every_public_type_barakoCMS_4_x_shipped_still_resolves_from_barakoCMS()
    {
        PublishedTypes.Should().HaveCount(232, "that is how many public types the seven 4.x packages hold between them");

        var unresolved = PublishedTypes
            .Where(name => Type.GetType($"{name}, barakoCMS") is null)
            .ToArray();

        unresolved.Should().BeEmpty(
            "a module built on any 4.x names these in assembly barakoCMS, so each needs "
          + "to be defined there or forwarded to where it lives now (barakoCMS/TypeForwards.cs)");
    }

    [Fact]
    public void The_moved_types_resolve_to_their_home_in_Abstractions()
    {
        var module = Type.GetType("barakoCMS.Modules.IBarakoModule, barakoCMS");
        var page = Type.GetType("barakoCMS.Models.PaginatedResponse`1, barakoCMS");

        module.Should().NotBeNull();
        module!.Assembly.GetName().Name.Should().Be("BarakoCMS.Abstractions");
        page.Should().NotBeNull("a generic type is forwarded by its open definition");
        page!.Assembly.GetName().Name.Should().Be("BarakoCMS.Abstractions");
    }
}
