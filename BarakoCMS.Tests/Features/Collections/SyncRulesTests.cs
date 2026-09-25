using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Collections;

/// <summary>
/// What a field rule makes of one flattened item, and which rules are refused on save (#1003).
/// </summary>
/// <remarks>
/// The rules are a pure static over a row, so the cases that need a value past any real source (a
/// count past decimal's range, a value that grows past the cap) are built here rather than served
/// by a stub provider.
/// </remarks>
public class SyncRulesTests
{
    private static Dictionary<string, string> Row(string value) => new() { ["id"] = value };

    [Fact]
    public void A_replace_rule_does_not_replace_text_another_replacement_wrote()
    {
        var rule = new SyncFieldRule { Path = "id", Replace = new() { ["."] = " / ", ["/"] = "-" } };

        SyncRules.Evaluate(rule, Row("a.b/c")).Should().Be("a / b-c");
    }

    [Fact]
    public void A_replace_rule_grows_a_value_once_and_writes_nothing_past_its_cap()
    {
        var chain = new SyncFieldRule
        {
            Path = "id",
            Replace = new()
            {
                ["a"] = new string('b', 200),
                ["b"] = new string('c', 200),
                ["c"] = new string('d', 200),
            },
        };

        SyncRules.Evaluate(chain, Row("a")).Should().Be(new string('b', 200), "the b's a replacement wrote are not read again");

        var growth = new SyncFieldRule { Path = "id", Replace = new() { ["a"] = new string('b', 200) } };

        SyncRules.Evaluate(growth, Row(new string('a', 500))).Should().Be(new string('b', 100_000), "500 times 200 is the cap");
        SyncRules.Evaluate(growth, Row(new string('a', 501))).Should().BeNull("one more a is past the cap");
    }

    [Fact]
    public void A_sum_or_ratio_past_the_range_of_a_decimal_writes_nothing()
    {
        var row = new Dictionary<string, string>
        {
            ["closed"] = "79228162514264337593543950335",
            ["open"] = "1",
            ["none"] = "0",
        };

        SyncRules.Evaluate(new SyncFieldRule { Sum = ["closed", "open"] }, row).Should().BeNull();
        SyncRules.Evaluate(new SyncFieldRule { Ratio = ["closed", "none"] }, row).Should().BeNull();
        SyncRules.Evaluate(new SyncFieldRule { Sum = ["open", "open"] }, row).Should().Be("2");
    }

    [Fact]
    public void A_sum_reads_every_path_it_adds()
    {
        SyncRules.PathsOf(new SyncFieldRule { Sum = ["closed", "open"] }).Should().Equal("closed", "open");
    }

    [Fact]
    public void A_whole_sum_is_written_without_the_scale_of_its_addends()
    {
        var row = new Dictionary<string, string> { ["closed"] = "3.0", ["open"] = "1", ["half"] = "0.50" };

        SyncRules.Evaluate(new SyncFieldRule { Sum = ["closed", "open"] }, row).Should().Be("4");
        SyncRules.Evaluate(new SyncFieldRule { Sum = ["closed", "half"] }, row).Should().Be("3.5");
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("ratio")]
    public void A_sum_or_ratio_of_an_array_path_is_refused_naming_the_field(string source)
    {
        List<string> paths = ["labels[].n", "open_issues"];
        var rule = source == "sum" ? new SyncFieldRule { Sum = paths } : new SyncFieldRule { Ratio = paths };

        SyncRules.ShapeProblem("total", rule).Should().Contain("'total'").And.Contain("array path");
    }

    [Fact]
    public void A_replace_on_a_constant_is_refused()
    {
        var rule = new SyncFieldRule { Const = "cms", Replace = new() { ["c"] = "k" } };

        SyncRules.ShapeProblem("product", rule).Should().Contain("only apply to a path");
    }
}
