using System.Text.Json;
using FluentAssertions;
using Marten;
using NSubstitute;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What an entry write accepts in a money field that declares a currency. The schema has no slug or
/// reference field, so the validator never reaches its session and no database is needed.
/// </summary>
/// <remarks>
/// Every refusal here is a value a money field with no currency accepts, and the last tests pin
/// that: opting in is what turns the rule on.
/// </remarks>
public class MoneyFieldValueTests
{
    private static readonly ContentValidatorService Validator = new(Substitute.For<IQuerySession>());

    private static FieldDefinition Total(string? currency, int? scale = null, params (string Rule, object Value)[] rules) =>
        new()
        {
            Name = "Total",
            DisplayName = "Total",
            Type = "money",
            Currency = currency,
            Scale = scale,
            ValidationRules = rules.ToDictionary(r => r.Rule, r => r.Value),
        };

    private static Task<(bool IsValid, List<string> Errors)> WriteAsync(object amount, FieldDefinition field) =>
        Validator.ValidateFieldsAsync(
            new ContentTypeDefinition { Name = "invoice", DisplayName = "Invoice", Fields = [field] },
            "invoice",
            new Dictionary<string, object> { ["Total"] = amount },
            existing: null);

    private static JsonElement Json(string number) => JsonDocument.Parse(number).RootElement.Clone();

    [Fact]
    public async Task An_amount_with_more_decimal_places_than_its_currency_has_is_refused_and_not_rounded()
    {
        var (isValid, errors) = await WriteAsync(10.005m, Total("USD"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("USD").And.Contain("2 decimal places").And.Contain("10.005");
    }

    [Fact]
    public async Task The_scale_is_the_currencys_own()
    {
        (await WriteAsync(12.5m, Total("USD"))).IsValid.Should().BeTrue();
        (await WriteAsync(12L, Total("USD"))).IsValid.Should().BeTrue();

        (await WriteAsync(100L, Total("JPY"))).IsValid.Should().BeTrue();
        (await WriteAsync(100.5m, Total("JPY"))).IsValid.Should().BeFalse("a yen has no minor unit");

        (await WriteAsync(1.234m, Total("KWD"))).IsValid.Should().BeTrue();
        (await WriteAsync(1.2345m, Total("KWD"))).IsValid.Should().BeFalse("a dinar has three decimal places");
    }

    [Fact]
    public async Task Trailing_zeros_past_the_scale_are_not_extra_decimal_places()
    {
        (await WriteAsync(12.500m, Total("USD"))).IsValid.Should().BeTrue();
        (await WriteAsync(12.501m, Total("USD"))).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task A_declared_scale_replaces_the_currencys()
    {
        (await WriteAsync(1.2345m, Total("USD", 4))).IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(1.23456m, Total("USD", 4));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("4 decimal places");
    }

    [Fact]
    public async Task Text_is_refused_where_a_currency_is_declared()
    {
        var (isValid, errors) = await WriteAsync("12.50", Total("USD"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("JSON number");
    }

    [Fact]
    public async Task A_binary_floating_point_value_is_refused_where_a_currency_is_declared()
    {
        var (isValid, errors) = await WriteAsync(12.5d, Total("USD"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("JSON number");
    }

    [Fact]
    public async Task A_json_number_is_read_as_a_decimal_and_held_to_the_scale()
    {
        (await WriteAsync(Json("12.34"), Total("USD"))).IsValid.Should().BeTrue();

        var (isValid, errors) = await WriteAsync(Json("12.345"), Total("USD"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("12.345");
    }

    [Fact]
    public async Task A_min_of_zero_refuses_a_negative_amount_that_fits_the_scale()
    {
        var (isValid, errors) = await WriteAsync(-5m, Total("USD", null, ("min", 0)));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Total").And.Contain("'min'");

        (await WriteAsync(0m, Total("USD", null, ("min", 0)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_money_field_with_no_currency_takes_everything_it_took_before()
    {
        var plain = Total(null);

        (await WriteAsync(10.005m, plain)).IsValid.Should().BeTrue();
        (await WriteAsync(1.23456789012m, plain)).IsValid.Should().BeTrue();
        (await WriteAsync("12.50", plain)).IsValid.Should().BeTrue();
        (await WriteAsync(12.5d, plain)).IsValid.Should().BeTrue();
        (await WriteAsync(Json("12.345"), plain)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_stored_field_whose_currency_no_save_would_accept_is_read_as_a_plain_number()
    {
        (await WriteAsync(10.005m, Total("usd"))).IsValid.Should().BeTrue();
        (await WriteAsync(10.005m, Total("QQQ"))).IsValid.Should().BeTrue();
        (await WriteAsync(10.005m, Total("USD", 40))).IsValid.Should().BeTrue();
    }
}
