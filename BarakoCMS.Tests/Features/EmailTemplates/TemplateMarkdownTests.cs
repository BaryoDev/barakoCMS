using System.Text.RegularExpressions;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// Markdown never sees a hole. An expression made of the characters markdown reads as markup still
/// renders the number it works out, and a loop around table rows repeats whole rows.
/// </summary>
[Collection("Integration1")]
public class TemplateMarkdownTests(IntegrationTestFixture fixture)
{
    private static Content Entry() => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "line",
        Status = ContentStatus.Published,
        Data = new() { ["Qty"] = 3, ["Price"] = 2.5m, ["Name"] = "a_b" },
    };

    private static string Render(string markdown)
    {
        var rendered = new RenderedTemplate("s", EmailTemplateRenderer.Markdown(markdown), []);
        return EmailTemplateRenderer.Resolve(rendered, Entry(), null).Body;
    }

    [Fact]
    public void An_expression_with_stars_brackets_quotes_pipes_and_underscores_in_a_paragraph_renders_its_value()
    {
        var html = Render(
            "Total {{= data.Qty * data.Price | money}} and {{= iif(data.Qty > 2, \"big|x\", \"small\")}} "
            + "and {{= concat(data.Name, \"_\", \"<b>\")}} and {{= data.Qty * 2 * 2}}.");

        html.Should().Contain("Total 7.50 and big|x and a_b_&lt;b&gt; and 12.");
        html.Should().NotContain("{{").And.NotContain("<b>");
    }

    [Fact]
    public void An_expression_with_the_same_characters_in_a_table_row_renders_in_its_own_cell()
    {
        var html = Render(
            "| Item | Total | Note |\n|---|---|---|\n"
            + "| {{data.Name}} | {{= data.Qty * data.Price | money}} | {{= iif(data.Qty > 2, \"a|b\", \"c\")}} |");

        Regex.Matches(html, "<td>[^<]*</td>").Select(m => m.Value).Should().Equal("<td>a_b</td>", "<td>7.50</td>", "<td>a|b</td>");
        Regex.Matches(html, "<tr>").Should().HaveCount(2);
    }

    [Fact]
    public void Text_that_looks_like_a_lifted_placeholder_is_left_as_text()
    {
        var html = Render("ph0x0x ph00000000000000000000000000x1x {{data.Name}}");

        html.Should().Contain("ph0x0x ph00000000000000000000000000x1x a_b");
    }

    [Fact]
    public void Loop_markers_around_a_table_body_row_sit_outside_the_row_in_the_markdown_output()
    {
        var html = EmailTemplateRenderer.Markdown(
            "| Product | Qty |\n|---|---|\n{{#each lines}}\n| {{row.Product.Name}} | {{row.Qty}} |\n{{/each}}");

        html.Should().Contain("{{#each lines}}<tr>").And.Contain("</tr>{{/each}}");
        html.Should().NotContain("<td>{{#each").And.NotContain("{{/each}}</td>");
    }

    [Fact]
    public async Task A_dataset_loop_around_a_table_row_sends_one_whole_row_for_each_line()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync(
            [new("Widget", 2, 10m, 20m), new("Gadget", 3, 4m, 12m), new("Gizmo", 1, 7.5m, 7.5m)],
            template: "| Product | Qty | Total |\n|---|---|---|\n{{#each lines}}\n| {{row.Product.Name}} | {{row.Qty}} | {{row.Total | money}} |\n{{/each}}");

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeTrue(result.Error);
        var body = sent!.Body;
        var table = Regex.Match(body, "<table>.*?</table>", RegexOptions.Singleline).Value;
        Regex.Matches(table, "<tr>").Should().HaveCount(4, "one header row and one for each line, not counting the layout's own rows");
        Regex.Matches(body, "<tr>\\s*(<td[^>]*>[^<]*</td>\\s*){3}</tr>").Select(m => Regex.Replace(Regex.Replace(m.Value, "<[^>]*>", " "), "\\s+", " ").Trim())
            .Should().Equal("Widget 2 20.00", "Gadget 3 12.00", "Gizmo 1 7.50");
        body.Should().NotContain("{{");
    }
}
