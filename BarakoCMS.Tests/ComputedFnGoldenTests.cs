using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Every computed function and the formula that says the same thing give the same stored value
/// over the same data: midpoints, negatives, a missing input, division by zero and overflow.
/// </summary>
/// <remarks>
/// The function rules are the reference. A formula is only a second way to write them down, so a
/// type can move from one to the other without a value changing.
/// </remarks>
public class ComputedFnGoldenTests
{
    private static FieldDefinition Plain(string name, string type) =>
        new() { Name = name, DisplayName = name, Type = type };

    private static FieldDefinition ByFn(string type, string fn, int? round, params string[] args) =>
        new() { Name = "T", DisplayName = "T", Type = type, Computed = new ComputedDefinition { Fn = fn, Args = args.ToList(), Round = round } };

    private static FieldDefinition ByExpression(string type, string expression, int? round) =>
        new() { Name = "T", DisplayName = "T", Type = type, Computed = new ComputedDefinition { Expression = expression, Round = round } };

    private static Dictionary<string, object> Data(params (string Key, object? Value)[] pairs) =>
        pairs.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!);

    /// <summary>Both rules over the same inputs, as the stored value each gives.</summary>
    private static (object? Fn, object? Expression) Both(
        string[] inputTypes, FieldDefinition fn, FieldDefinition expression, Dictionary<string, object> data)
    {
        var inputs = new[] { Plain("A", inputTypes[0]), Plain("B", inputTypes[1]) };
        return (
            ComputedFields.Compute(fn, data, [.. inputs, fn]),
            ComputedFields.Compute(expression, data, [.. inputs, expression]));
    }

    private static readonly decimal Max = decimal.MaxValue;

    public static TheoryData<decimal?, decimal?> NumberPairs => new()
    {
        { 7.5m, 2.5m },
        { -1.5m, 0.5m },
        { 2.5m, 1m },
        { -2.5m, 1m },
        { 0.005m, 1m },
        { -0.005m, 1m },
        { 1.005m, 1m },
        { 0m, 0m },
        { 1m, 0m },
        { -7m, 0m },
        { Max, 1m },
        { Max, 2m },
        { -Max, 2m },
        { 9223372036854775807m, 1m },
        { 1m, null },
        { null, 2m },
        { null, null },
    };

    [Theory]
    [MemberData(nameof(NumberPairs))]
    public void A_formula_over_two_numbers_matches_the_function(decimal? a, decimal? b)
    {
        var data = Data(("A", a), ("B", b));
        var rules = new (string Fn, string Expression)[]
        {
            ("add", "A + B"),
            ("subtract", "A - B"),
            ("multiply", "A * B"),
            ("divide", "A / B"),
            ("min", "min(A, B)"),
            ("max", "max(A, B)"),
        };

        foreach (var (fn, expression) in rules)
        {
            foreach (var type in new[] { "decimal", "money", "int" })
            {
                foreach (int? round in new int?[] { null, 0, 2 })
                {
                    var (byFn, byExpression) = Both(
                        ["decimal", "decimal"], ByFn(type, fn, round, "A", "B"), ByExpression(type, expression, round), data);

                    byExpression.Should().Be(byFn, $"{fn} as {type} rounded to {round?.ToString() ?? "nothing"} over {a} and {b}");
                }
            }
        }
    }

    [Theory]
    [InlineData(2.5, 0)]
    [InlineData(-2.5, 0)]
    [InlineData(2.345, 2)]
    [InlineData(-2.345, 2)]
    [InlineData(0.5, 0)]
    [InlineData(1.005, 2)]
    [InlineData(7, 3)]
    public void Round_matches_the_function_at_midpoints_and_negatives(double input, int places)
    {
        var value = (decimal)input;
        var inputs = new[] { Plain("A", "decimal") };
        var data = Data(("A", value));

        var byFn = ComputedFields.Compute(ByFn("decimal", "round", places, "A"), data, inputs);
        var rule = ByExpression("decimal", $"round(A, {places})", null);
        var byExpression = ComputedFields.Compute(rule, data, [.. inputs, rule]);

        byExpression.Should().Be(byFn);
        byExpression.Should().NotBeNull();
    }

    [Fact]
    public void Round_with_no_places_matches_the_function()
    {
        var data = Data(("A", 2.5m));
        var rule = ByExpression("decimal", "round(A)", null);

        ComputedFields.Compute(rule, data, [Plain("A", "decimal"), rule])
            .Should().Be(ComputedFields.Compute(ByFn("decimal", "round", null, "A"), data));
    }

    [Theory]
    [InlineData("Ana", "Reyes")]
    [InlineData("Ana", null)]
    [InlineData(null, "Reyes")]
    [InlineData(null, null)]
    [InlineData("", "x")]
    [InlineData("   ", "x")]
    [InlineData("a", " ")]
    [InlineData("1", "2")]
    public void Concat_and_coalesce_match_the_functions(string? a, string? b)
    {
        var data = Data(("A", a), ("B", b));

        foreach (var (fn, expression) in new[] { ("concat", "concat(A, B)"), ("coalesce", "coalesce(A, B)") })
        {
            var (byFn, byExpression) = Both(
                ["string", "string"], ByFn("string", fn, null, "A", "B"), ByExpression("string", expression, null), data);

            byExpression.Should().Be(byFn, $"{fn} over '{a}' and '{b}'");
        }
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-04")]
    [InlineData("2026-10-04", "2026-10-01")]
    [InlineData("2026-10-19T08:00:00Z", "2026-10-19T16:20:00Z")]
    [InlineData("2026-10-19T16:20:00Z", "2026-10-19T08:00:00Z")]
    [InlineData("2026-10-19T08:00:00Z", "2026-10-19T08:00:00Z")]
    [InlineData("2026-02-28", "2028-02-29")]
    [InlineData("2026-10-19T08:00:00Z", null)]
    [InlineData(null, "2026-10-19T08:00:00Z")]
    public void Days_and_hours_match_the_functions(string? from, string? to)
    {
        var data = Data(("A", from), ("B", to));

        foreach (var (fn, expression) in new[] { ("days", "days(A, B)"), ("hours", "hours(A, B)") })
        {
            foreach (int? round in new int?[] { null, 0, 2 })
            {
                var (byFn, byExpression) = Both(
                    ["datetime", "datetime"], ByFn("decimal", fn, round, "A", "B"), ByExpression("decimal", expression, round), data);

                byExpression.Should().Be(byFn, $"{fn} rounded to {round?.ToString() ?? "nothing"} from {from} to {to}");
            }
        }
    }

    /// <summary>
    /// There is no minutes function in a formula, so duration is written as hours times 60. A
    /// whole number of minutes is the same either way once it is rounded to one.
    /// </summary>
    [Theory]
    [InlineData("2026-10-19T08:00:00Z", "2026-10-19T16:20:00Z")]
    [InlineData("2026-10-19T16:20:00Z", "2026-10-19T08:00:00Z")]
    [InlineData("2026-10-01", "2026-10-04")]
    [InlineData("2026-10-19T08:00:00Z", null)]
    public void Duration_in_whole_minutes_is_hours_times_sixty(string? from, string? to)
    {
        var data = Data(("A", from), ("B", to));

        var (byFn, byExpression) = Both(
            ["datetime", "datetime"], ByFn("int", "duration", null, "A", "B"), ByExpression("int", "hours(A, B) * 60", null), data);

        byExpression.Should().Be(byFn);
    }

    [Fact]
    public void A_function_rule_and_a_formula_give_the_same_value_through_apply()
    {
        var fnFields = new[] { Plain("A", "decimal"), Plain("B", "decimal"), ByFn("decimal", "multiply", 2, "A", "B") };
        var exprFields = new[] { Plain("A", "decimal"), Plain("B", "decimal"), ByExpression("decimal", "A * B", 2) };
        var one = Data(("A", 1.005m), ("B", 3m));
        var two = Data(("A", 1.005m), ("B", 3m));

        ComputedFields.Apply(fnFields, one);
        ComputedFields.Apply(exprFields, two);

        two.Should().ContainKey("T");
        two["T"].Should().Be(one["T"]);
    }
}
