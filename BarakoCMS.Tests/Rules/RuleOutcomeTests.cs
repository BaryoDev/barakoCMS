using barakoCMS.Core.Interfaces;
using FluentAssertions;

namespace BarakoCMS.Tests.Rules;

/// <summary>The 422 body is tested end to end in RuleCallSiteTests.</summary>
public class RuleOutcomeTests
{
    [Fact]
    public void RuleFailed_carries_its_message()
    {
        var result = ContentTransitionResult.RuleFailed("no");

        result.Outcome.Should().Be(ContentTransitionOutcome.RuleFailed);
        ((int)result.Outcome).Should().Be(5);
        result.Errors.Single().Should().Be("no");
    }
}
