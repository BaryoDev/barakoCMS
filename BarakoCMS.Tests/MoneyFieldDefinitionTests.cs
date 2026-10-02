using FluentAssertions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The currency and scale a money field may declare, checked when the type is saved. A field that
/// declares neither is the money field that has always existed and is not touched.
/// </summary>
public class MoneyFieldDefinitionTests
{
    private static readonly ContentTypeValidatorService Validator = new();

    private static (bool IsValid, List<string> Errors) Save(string type, string? currency, int? scale = null) =>
        Validator.Validate("invoice", "Invoice",
        [
            new FieldDefinition
            {
                Name = "Total",
                DisplayName = "Total",
                Type = type,
                Currency = currency,
                Scale = scale,
            },
        ]);

    [Fact]
    public void A_money_field_with_no_currency_is_accepted_as_it_always_was()
    {
        Save("money", null).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("usd")]
    [InlineData("EU")]
    [InlineData("USDX")]
    [InlineData("")]
    [InlineData("U$D")]
    [InlineData("<b>")]
    public void A_currency_that_is_not_three_capital_letters_is_refused_without_echoing_it(string currency)
    {
        var (isValid, errors) = Save("money", currency);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("three capital letters");
        if (currency.Length > 0)
            errors[0].Should().NotContain(currency);
    }

    [Fact]
    public void A_code_the_built_in_list_does_not_hold_needs_its_own_scale()
    {
        var (isValid, errors) = Save("money", "QQQ");

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("QQQ").And.Contain("Declare a scale");

        Save("money", "QQQ", 8).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void A_scale_outside_zero_to_eight_is_refused(int scale)
    {
        var (isValid, errors) = Save("money", "USD", scale);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("from 0 to 8");
    }

    [Fact]
    public void A_scale_with_no_currency_is_refused()
    {
        var (isValid, errors) = Save("money", null, 2);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("no currency");
    }

    [Theory]
    [InlineData("decimal")]
    [InlineData("int")]
    [InlineData("string")]
    public void A_currency_on_a_field_that_is_not_money_is_refused(string type)
    {
        var (isValid, errors) = Save(type, "USD");

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("not money");
    }
}
