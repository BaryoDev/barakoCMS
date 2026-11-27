using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// An invoice rendered from entries with a template and a saved query, no code: the customer, a row
/// for each line, and a subtotal, tax and total from the rows.
/// </summary>
[Collection("Integration1")]
public class InvoiceTemplateTests(IntegrationTestFixture fixture)
{
    private static InvoiceKit.Line Widget => new("Widget", 2, 10.00m, 20.00m);

    /// <summary>One line is priced to three decimals, so its total is 12.375 and the sum 39.875.</summary>
    private static InvoiceKit.Line[] ThreeLines =>
    [
        Widget,
        new("Gadget", 3, 4.125m, 12.375m),
        new("Gizmo", 1, 7.50m, 7.50m),
    ];

    [Fact]
    public async Task The_invoice_renders_the_exact_text_with_totals_rounded_half_away_from_zero()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync(ThreeLines);

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeTrue(result.Error);
        sent.Should().NotBeNull();
        sent!.Subject.Should().Be("Invoice INV-0007");
        InvoiceKit.TextOf(sent.Body).Should().Equal(
            "Invoice INV-0007 for Acme Ltd",
            "Widget  2 x 10.00 = 20.00",
            "Gadget  3 x 4.13 = 12.38",
            "Gizmo  1 x 7.50 = 7.50",
            "Subtotal 39.88",
            "Tax 4.79",
            "Total 44.66");
    }

    [Fact]
    public async Task An_archived_line_is_left_out_of_the_rows_and_the_totals()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([.. ThreeLines, new("Old stock", 9, 99m, 891m, ContentStatus.Archived)]);

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeTrue(result.Error);
        sent!.Body.Should().NotContain("Old stock").And.NotContain("891");
        InvoiceKit.TextOf(sent.Body).Should().HaveCount(7).And.Contain("Total 44.66");
    }

    [Fact]
    public async Task A_product_named_as_a_script_is_escaped_in_the_html_part()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([new("<script>alert(1)</script>", 1, 5m, 5m)]);

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeTrue(result.Error);
        sent!.Body.Should().NotContain("<script>").And.NotContain("</script>");
        sent.Body.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public async Task Two_hundred_lines_render_with_their_total()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([.. Enumerable.Range(1, 200).Select(i => new InvoiceKit.Line($"Item {i:000}", 1, 1m, 1m))]);

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeTrue(result.Error);
        var text = InvoiceKit.TextOf(sent!.Body);
        text.Should().HaveCount(1 + 200 + 3);
        text.Should().Contain("Item 200  1 x 1.00 = 1.00").And.Contain("Subtotal 200.00");
    }

    [Fact]
    public async Task Two_hundred_and_one_lines_fail_with_the_message_and_send_nothing()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([.. Enumerable.Range(1, 201).Select(i => new InvoiceKit.Line($"Item {i:000}", 1, 1m, 1m))]);

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same invoice has the same lines on the next try");
        result.Error.Should().Contain("lines").And.Contain("more than 200 rows");
        sent.Should().BeNull("a cut invoice is wrong, so none is sent");
    }
}
