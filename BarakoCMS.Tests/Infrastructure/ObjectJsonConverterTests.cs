using System.Text.Json;
using FluentAssertions;
using Xunit;
using barakoCMS.Infrastructure.Serialization;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// Pure serializer-level cover for <see cref="ObjectJsonConverter"/>. The Marten round-trip is
/// exercised separately in <see cref="ContentDataDecimalTests"/>; these run without a database so the
/// number rules are pinned down cheaply.
/// </summary>
public class ObjectJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions();
        o.Converters.Add(new ObjectJsonConverter());
        return o;
    }

    private static Dictionary<string, object> Read(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(json, Options)!;

    [Fact]
    public void Fractional_numbers_become_decimal_not_double()
    {
        var result = Read("""{"Amount":1234.56}""");

        result["Amount"].Should().BeOfType<decimal>("money must not degrade to binary floating point");
        result["Amount"].Should().Be(1234.56m);
    }

    [Fact]
    public void Whole_numbers_stay_integral()
    {
        var result = Read("""{"Count":42}""");

        result["Count"].Should().BeOfType<long>("ids and counts shouldn't become 42.0m");
        result["Count"].Should().Be(42L);
    }

    [Fact]
    public void Numbers_outside_decimal_range_fall_back_to_double_rather_than_throwing()
    {
        Read("""{"Huge":1e308}""")["Huge"].Should().BeOfType<double>();
    }

    [Fact]
    public void Strings_bools_and_null_are_unwrapped()
    {
        var r = Read("""{"S":"hi","B":true,"N":null}""");

        r["S"].Should().Be("hi");
        r["B"].Should().Be(true);
        r["N"].Should().BeNull();
    }

    [Fact]
    public void Nested_objects_are_plain_dictionaries_all_the_way_down()
    {
        var result = Read("""{"Outer":{"Inner":{"Money":9.99}}}""");

        // The bug this prevents: ConditionEvaluator type-checked for Dictionary<string, object> and
        // silently got a JsonElement, so every persisted conditional permission rule failed closed.
        var outer = result["Outer"].Should().BeOfType<Dictionary<string, object>>().Subject;
        var inner = outer["Inner"].Should().BeOfType<Dictionary<string, object>>().Subject;
        inner["Money"].Should().BeOfType<decimal>().And.Be(9.99m);
    }

    [Fact]
    public void Arrays_are_plain_lists_with_the_same_number_rules()
    {
        var list = Read("""{"Lines":[1, 2.5, "x", {"N": 0.1}]}""")["Lines"]
            .Should().BeOfType<List<object>>().Subject;

        list[0].Should().Be(1L);
        list[1].Should().Be(2.5m);
        list[2].Should().Be("x");
        list[3].Should().BeOfType<Dictionary<string, object>>()
            .Which["N"].Should().Be(0.1m);
    }

    [Fact]
    public void Decimal_survives_a_full_serialize_deserialize_cycle()
    {
        var original = new Dictionary<string, object> { ["Amount"] = 1234.56m };

        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json, Options)!;

        result["Amount"].Should().BeOfType<decimal>().And.Be(1234.56m);
    }

    [Fact]
    public void Nested_decimals_survive_a_write_then_read_cycle()
    {
        var original = new Dictionary<string, object>
        {
            ["Lines"] = new List<object>
            {
                new Dictionary<string, object> { ["Debit"] = 10.05m, ["Credit"] = 0m },
            },
        };

        var json = JsonSerializer.Serialize(original, Options);
        var result = JsonSerializer.Deserialize<Dictionary<string, object>>(json, Options)!;

        var lines = result["Lines"].Should().BeOfType<List<object>>().Subject;
        var line = lines[0].Should().BeOfType<Dictionary<string, object>>().Subject;
        line["Debit"].Should().BeOfType<decimal>().And.Be(10.05m);
    }

    [Fact]
    public void Repeated_addition_after_round_trip_stays_exact()
    {
        // 0.1 has no exact binary representation. Ten of them must still be exactly 1.0m.
        decimal sum = 0;
        for (var i = 0; i < 10; i++)
            sum += (decimal)Read("""{"Amount":0.1}""")["Amount"];

        sum.Should().Be(1.0m, "accumulating a ledger's lines must not drift");
    }

    [Fact]
    public void High_precision_values_do_not_spill_to_double()
    {
        Read("""{"Max":79228162514264337593543950335}""")["Max"]
            .Should().BeOfType<decimal>("decimal.MaxValue must survive");
    }
}
