using System.Text.Json;
using FluentAssertions;
using Marten;
using NSubstitute;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Serialization;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// An int field holds any 64-bit integer, whatever shape the value arrives in (#706).
/// </summary>
/// <remarks>
/// A request body reaches the registry as a long, because <see cref="ObjectJsonConverter"/> reads
/// every whole number that fits Int64 as one, and a fraction or a larger number as a decimal. A raw
/// <see cref="JsonElement"/> and numeric text reach it from modules and stored values, and used to
/// stop at Int32. These cases are built so the old Int32 check and the Int64 one answer differently.
/// </remarks>
public class IntFieldRangeTests
{
    private const long PastInt32 = 3_000_000_000;
    private const string PastInt64 = "9223372036854775808";

    private static JsonElement Json(string number) => JsonDocument.Parse(number).RootElement.Clone();

    private static readonly ContentValidatorService Validator = new(Substitute.For<IQuerySession>());

    private static Task<(bool IsValid, List<string> Errors)> WriteAsync(object value, string type = "int") =>
        Validator.ValidateFieldsAsync(
            new ContentTypeDefinition
            {
                Name = "counter",
                DisplayName = "Counter",
                Fields = [new FieldDefinition { Name = "Views", DisplayName = "Views", Type = type }],
            },
            "counter",
            new Dictionary<string, object> { ["Views"] = value },
            existing: null);

    private static Dictionary<string, object> ReadBody(string json)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectJsonConverter());
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json, options)!;
    }

    [Theory]
    [InlineData("int")]
    [InlineData("integer")]
    [InlineData("number")]
    public void A_value_past_Int32_is_an_int_in_every_shape(string type)
    {
        FieldTypeRegistry.IsValidValue(type, PastInt32).Should().BeTrue();
        FieldTypeRegistry.IsValidValue(type, Json("3000000000")).Should().BeTrue();
        FieldTypeRegistry.IsValidValue(type, "3000000000").Should().BeTrue();
        FieldTypeRegistry.IsValidValue(type, long.MaxValue).Should().BeTrue();
        FieldTypeRegistry.IsValidValue(type, Json(long.MinValue.ToString())).Should().BeTrue();
        FieldTypeRegistry.IsValidValue(type, long.MinValue.ToString()).Should().BeTrue();
    }

    [Fact]
    public void A_value_past_Int64_is_refused_in_every_shape()
    {
        FieldTypeRegistry.IsValidValue("int", Json(PastInt64)).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", PastInt64).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", ulong.MaxValue).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", ReadBody($"{{\"v\":{PastInt64}}}")["v"]).Should().BeFalse();
    }

    [Fact]
    public void A_fraction_is_refused_in_every_shape()
    {
        FieldTypeRegistry.IsValidValue("int", Json("1.5")).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", "1.5").Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", 1.5m).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", 1.5d).Should().BeFalse();
        FieldTypeRegistry.IsValidValue("int", ReadBody("{\"v\":1.5}")["v"]).Should().BeFalse();
    }

    [Fact]
    public void A_stored_Int32_value_is_still_an_int()
    {
        FieldTypeRegistry.IsValidValue("int", 42).Should().BeTrue();
        FieldTypeRegistry.IsValidValue("int", Json("42")).Should().BeTrue();
        FieldTypeRegistry.IsValidValue("int", "-42").Should().BeTrue();
    }

    [Fact]
    public void A_request_body_carries_a_value_past_Int32_as_a_long_and_one_past_Int64_as_a_decimal()
    {
        var body = ReadBody($"{{\"inside\":3000000000,\"past\":{PastInt64},\"fraction\":1.5}}");

        body.Should().HaveCount(3);
        body["inside"].Should().BeOfType<long>().Which.Should().Be(PastInt32);
        body["past"].Should().BeOfType<decimal>();
        body["fraction"].Should().BeOfType<decimal>();
    }

    [Fact]
    public async Task An_entry_write_takes_a_value_past_Int32_and_refuses_one_past_Int64_naming_the_field()
    {
        (await WriteAsync(PastInt32)).IsValid.Should().BeTrue();
        (await WriteAsync(Json("3000000000"))).IsValid.Should().BeTrue();

        var (pastValid, pastErrors) = await WriteAsync(Json(PastInt64));
        pastValid.Should().BeFalse();
        pastErrors.Should().HaveCount(1);
        pastErrors[0].Should().Contain("Views").And.Contain("int");

        var (fractionValid, fractionErrors) = await WriteAsync(1.5m);
        fractionValid.Should().BeFalse();
        fractionErrors.Should().HaveCount(1);
        fractionErrors[0].Should().Contain("Views").And.Contain("int");
    }

    [Fact]
    public void Integer_and_number_are_the_int_type_and_decimal_is_not()
    {
        FieldTypeRegistry.IsIntegerType("int").Should().BeTrue();
        FieldTypeRegistry.IsIntegerType("Integer").Should().BeTrue();
        FieldTypeRegistry.IsIntegerType("number").Should().BeTrue();
        FieldTypeRegistry.IsIntegerType("decimal").Should().BeFalse();
        FieldTypeRegistry.IsIntegerType("money").Should().BeFalse();
        FieldTypeRegistry.IsIntegerType(null).Should().BeFalse();
    }
}
