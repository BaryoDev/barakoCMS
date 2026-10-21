using barakoCMS.Infrastructure.Services;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>A host that builds a ContentCreator itself, with the arguments it had before rules existed, still can.</summary>
public class ContentCreatorConstructorTests
{
    [Fact]
    public void A_creator_can_be_built_without_a_rule_engine()
    {
        var creator = new ContentCreator(null!, null!, null!, null!, null!);

        creator.Should().NotBeNull();
    }
}
