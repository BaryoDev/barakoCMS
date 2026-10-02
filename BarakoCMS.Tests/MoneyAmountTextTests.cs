using System.Text.Json;
using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What counts as plain decimal text for a writer that only has text: a spreadsheet cell, a
/// workflow parameter, a form input. Anything else stays text, so the validator refuses it.
/// </summary>
public class MoneyAmountTextTests
{
    private static FieldDefinition Field(string type, string? currency) =>
        new() { Name = "Total", DisplayName = "Total", Type = type, Currency = currency };

    [Theory]
    [InlineData("12.50", "12.50")]
    [InlineData("12", "12")]
    [InlineData("0.5", "0.5")]
    [InlineData("-3.20", "-3.20")]
    [InlineData("+7", "7")]
    [InlineData("  12.50  ", "12.50")]
    [InlineData("12.345", "12.345")]
    public void Plain_decimal_text_is_read_as_the_amount_it_spells(string text, string expected)
    {
        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), text, out var amount).Should().BeTrue();

        amount.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("1,250.00")]
    [InlineData("1 250")]
    [InlineData("1e3")]
    [InlineData("$12.50")]
    [InlineData("12.")]
    [InlineData(".5")]
    [InlineData("1.2.3")]
    [InlineData("--1")]
    [InlineData("1-")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999999999999999999999999999")]
    public void Anything_else_is_left_as_text(string text)
    {
        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), text, out _).Should().BeFalse();
    }

    [Fact]
    public void A_json_string_is_read_the_same_way_and_a_number_is_not_text()
    {
        var text = JsonDocument.Parse("\"12.50\"").RootElement.Clone();
        var number = JsonDocument.Parse("12.50").RootElement.Clone();

        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), text, out var amount).Should().BeTrue();
        amount.Should().Be(12.50m);

        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), number, out _).Should().BeFalse();
        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), 12.50m, out _).Should().BeFalse();
        FieldTypeRegistry.TryReadAmountText(Field("money", "USD"), null, out _).Should().BeFalse();
    }

    [Fact]
    public void Text_is_left_alone_for_a_field_that_declares_no_usable_currency()
    {
        FieldTypeRegistry.TryReadAmountText(Field("money", null), "12.50", out _).Should().BeFalse();
        FieldTypeRegistry.TryReadAmountText(Field("money", "usd"), "12.50", out _).Should().BeFalse();
        FieldTypeRegistry.TryReadAmountText(Field("decimal", "USD"), "12.50", out _).Should().BeFalse();
        FieldTypeRegistry.TryReadAmountText(Field("string", null), "12.50", out _).Should().BeFalse();
    }
}
