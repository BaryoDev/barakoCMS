using System.Text.Json;
using barakoCMS.Infrastructure.Migrations;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// <c>db-migrate</c> lists stored texts that will evaluate. It reads them the way the renderer
/// does, so a value written with JSON escapes is found as well.
/// </summary>
public class StoredExpressionTemplatesTests
{
    [Theory]
    [InlineData("Total {{= 1 + 2}}")]
    [InlineData("Total {{  = 1 + 2 | money}}")]
    [InlineData("{{#each lines}}{{= row.Qty}}{{/each}}")]
    public void A_text_holding_an_expression_is_found(string text) =>
        ExpressionTemplates.Holds(text).Should().BeTrue();

    [Theory]
    [InlineData("Hi {{data.Name}}")]
    [InlineData("{{data.Total | money}}")]
    [InlineData("{{#each data.Items}}{{data.Name}}{{/each}}")]
    [InlineData("{{= is not closed")]
    [InlineData("")]
    public void A_text_with_no_expression_is_not_found(string text) =>
        ExpressionTemplates.Holds(text).Should().BeFalse();

    [Theory]
    [InlineData("\"\\u007b\\u007b= 1 + 2}}\"")]
    [InlineData("\"\\u007b\\u007b = 1 + 2\\u007d\\u007d\"")]
    [InlineData("\"Total \\u007b\\u007b= 1\\u007d}\"")]
    public void A_value_written_with_json_escapes_is_found_as_it_decodes(string json)
    {
        using var document = JsonDocument.Parse(json);

        ExpressionTemplates.Holds(document.RootElement.Clone()).Should().BeTrue();
    }

    [Fact]
    public void An_expression_inside_JSON_held_in_a_parameter_is_found_with_or_without_escapes()
    {
        ExpressionTemplates.Holds("[{\"Type\":\"Email\",\"Parameters\":{\"Body\":\"{{= 1 + 2}}\"}}]").Should().BeTrue();
        ExpressionTemplates.Holds("[{\"Type\":\"Email\",\"Parameters\":{\"Body\":\"\\u007b\\u007b= 1 + 2\\u007d\\u007d\"}}]").Should().BeTrue();
        ExpressionTemplates.Holds("[{\"Type\":\"Email\",\"Parameters\":{\"Body\":\"{{data.Name}}\"}}]").Should().BeFalse();
    }

    [Fact]
    public void A_value_inside_a_list_or_object_is_found()
    {
        ExpressionTemplates.Holds(new Dictionary<string, object> { ["a"] = new List<object> { "x", "{{= 1}}" } }).Should().BeTrue();
        ExpressionTemplates.Holds(new List<object> { "x", "y" }).Should().BeFalse();
    }

    [Fact]
    public void A_text_over_the_scan_cap_is_listed_since_it_cannot_be_read_in_full()
    {
        var text = new string('a', TemplateHoles.MaxScanLength + 1);

        ExpressionTemplates.Holds(text).Should().BeTrue();
        ExpressionTemplates.Holds(JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone()).Should().BeTrue();
    }
}
