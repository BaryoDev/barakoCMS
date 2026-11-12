using System.Text.Json;
using barakoCMS.Features.WorkflowRuns;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.WorkflowRuns;

/// <summary>What counts as a title: text only, trimmed, with hidden characters removed, and unchanged by the caller's rules.</summary>
public class RunContentTitleTextTests
{
    private static string? Title(object? stored, object? shown = null) =>
        RunContentTitles.TitleOf(
            new Dictionary<string, object> { ["Title"] = stored! },
            new Dictionary<string, object> { ["Title"] = shown ?? stored! });

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void A_string_is_a_title_and_so_is_a_json_string()
    {
        Title("Quarterly report").Should().Be("Quarterly report");
        Title(Json("\"Quarterly report\"")).Should().Be("Quarterly report");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{\"a\":1}")]
    [InlineData("[\"x\"]")]
    public void A_value_that_is_not_text_is_no_title(string raw) => Title(Json(raw)).Should().BeNull();

    [Fact]
    public void A_number_or_a_list_is_no_title()
    {
        Title(42).Should().BeNull();
        Title(new List<string> { "x" }).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("\u200B\u202E")]
    public void A_blank_title_is_no_title(string text) => Title(text).Should().BeNull();

    [Fact]
    public void A_title_is_trimmed()
    {
        Title("  Quarterly report \n").Should().Be("Quarterly report");
    }

    [Theory]
    [InlineData("abc\u202Edef", "abcdef")]
    [InlineData("abc\u2066def\u2069", "abcdef")]
    [InlineData("a\u2028b\u2029c", "abc")]
    [InlineData("a\u0000b\u0007c", "abc")]
    [InlineData("a\u200Bb", "ab")]
    public void Control_format_and_separator_characters_are_removed(string stored, string expected) =>
        Title(stored).Should().Be(expected);

    [Fact]
    public void A_title_the_caller_rules_changed_is_no_title()
    {
        Title("Layoff plan", "L*********").Should().BeNull();
    }

    [Fact]
    public void A_title_that_was_removed_is_no_title()
    {
        RunContentTitles.TitleOf(
            new Dictionary<string, object> { ["Title"] = "Layoff plan" },
            new Dictionary<string, object>()).Should().BeNull();
    }
}
