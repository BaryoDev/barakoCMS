using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

/// <summary>A numbering rule may name its series with the branch code, and may let a client take numbers.</summary>
public class SeriesTemplateTests
{
    private static ContentTypeDefinition Def(string? template) => new()
    {
        Name = "Customer",
        Fields = [new FieldDefinition { Name = "CustomerNo", DisplayName = "Customer no", Type = "string" }],
        Numbering = new NumberingRule { Field = "CustomerNo", Prefix = "P-", Padding = 4, SeriesTemplate = template },
    };

    [Fact]
    public void No_template_keeps_the_default_series() =>
        NumberingRules.SeriesFor(Def(null), Def(null).Numbering!, "br1").Name.Should().Be("type.customer");

    [Fact]
    public void Template_takes_the_lowercase_branch_code_and_type() =>
        NumberingRules.SeriesFor(Def("{branch}.customer"), Def("{branch}.customer").Numbering!, "BR1").Name.Should().Be("br1.customer");

    [Fact]
    public void A_template_that_needs_a_branch_with_none_given_is_an_error()
    {
        var d = Def("{branch}.customer");
        NumberingRules.DefinitionErrors(d, d.Numbering!).Should().BeEmpty();
        var act = () => NumberingRules.SeriesFor(d, d.Numbering!, null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*branch*");
    }

    [Fact]
    public void A_client_allocated_series_cannot_reset()
    {
        var d = Def(null);
        d.Numbering!.ClientAllocated = true;
        d.Numbering.Reset = NumberReset.Yearly;
        NumberingRules.DefinitionErrors(d, d.Numbering).Should().Contain("A client-allocated series cannot reset.");
    }

    [Fact]
    public void A_template_that_makes_an_invalid_series_name_is_refused()
    {
        var d = Def("{branch}/customer");
        NumberingRules.DefinitionErrors(d, d.Numbering!).Should().Contain(e => e.Contains("series"));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("a%b")]
    public void A_branch_code_that_cannot_be_part_of_a_series_name_is_refused_not_thrown_as_an_argument_error(string code)
    {
        var d = Def("{branch}.customer");
        var act = () => NumberingRules.SeriesFor(d, d.Numbering!, code);
        act.Should().Throw<InvalidOperationException>().WithMessage("*branch code*");
    }

    [Fact]
    public void A_template_name_over_the_limit_is_refused_and_not_cut_short()
    {
        var d = Def("{branch}.customer");
        var act = () => NumberingRules.SeriesFor(d, d.Numbering!, new string('a', 70));
        act.Should().Throw<InvalidOperationException>().WithMessage("*characters*");
    }

    [Fact]
    public void A_client_allocated_series_cannot_use_hilo()
    {
        var d = Def(null);
        d.Numbering!.ClientAllocated = true;
        d.Numbering.Strategy = NumberStrategy.HiLo;
        NumberingRules.DefinitionErrors(d, d.Numbering).Should().Contain(e => e.Contains("hiLo"));
    }

    [Theory]
    [InlineData(NumberStrategy.Gapless)]
    [InlineData(NumberStrategy.Sequence)]
    public void A_client_allocated_series_may_use_a_strategy_that_counts_exactly(NumberStrategy strategy)
    {
        var d = Def(null);
        d.Numbering!.ClientAllocated = true;
        d.Numbering.Strategy = strategy;
        NumberingRules.DefinitionErrors(d, d.Numbering).Should().BeEmpty();
    }
}
