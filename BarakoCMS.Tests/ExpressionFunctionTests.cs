using System.Globalization;
using barakoCMS.Core.Expressions;
using R = barakoCMS.Core.Expressions.Rules;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.ExpressionKit;

namespace BarakoCMS.Tests;

/// <summary>Every function of the formula language, on a fixed clock (27 November 2026, 10:30 UTC).</summary>
public class ExpressionFunctionTests
{
    [Theory]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(-2.5)", "-3")]
    [InlineData("round(3.5)", "4")]
    [InlineData("round(1.005, 2)", "1.01")]
    [InlineData("round(1.2345, 3)", "1.235")]
    [InlineData("round(-1.005, 2)", "-1.01")]
    [InlineData("round(1234.5678, 0)", "1235")]
    [InlineData("round(null)", "null")]
    [InlineData("floor(-1.5)", "-2")]
    [InlineData("floor(1.9)", "1")]
    [InlineData("ceiling(1.2)", "2")]
    [InlineData("ceiling(-1.2)", "-1")]
    [InlineData("abs(-3)", "3")]
    [InlineData("abs(3)", "3")]
    [InlineData("min(3, 1, 2)", "1")]
    [InlineData("max(3, 1, 2)", "3")]
    [InlineData("min(1, null)", "null")]
    [InlineData("max(null, 5)", "null")]
    public void Math_functions(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Theory]
    [InlineData("iif(1 < 2, \"a\", \"b\")", "'a'")]
    [InlineData("iif(1 > 2, \"a\", \"b\")", "'b'")]
    [InlineData("iif(null, \"a\", \"b\")", "'b'")]
    [InlineData("iif(Active, 1, 2)", "2")]
    [InlineData("switch(1 = 2, \"x\", 2 = 2, \"y\", \"z\")", "'y'")]
    [InlineData("switch(1 = 2, \"x\", 2 = 3, \"y\", \"z\")", "'z'")]
    [InlineData("switch(null, \"x\", \"z\")", "'z'")]
    [InlineData("coalesce(null, \"\", \"  \", \"x\")", "'x'")]
    [InlineData("coalesce(null, 0)", "0")]
    [InlineData("coalesce(null, null)", "null")]
    [InlineData("coalesce(Name, \"none\")", "'none'")]
    [InlineData("isblank(null)", "true")]
    [InlineData("isblank(\"  \")", "true")]
    [InlineData("isblank(\"a\")", "false")]
    [InlineData("isblank(0)", "false")]
    [InlineData("isblank(Name)", "true")]
    public void Logic_functions(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Fact]
    public void Lazy_functions_do_not_evaluate_the_branch_they_skip()
    {
        var result = Run("iif(true, 1, 1 / 0)");
        result.Value.Should().Be(1m);
        result.Note.Should().BeNull("the skipped branch never ran");

        Run("coalesce(1, 1 / 0)").Note.Should().BeNull();
        Run("true or 1 / 0 = 1").Note.Should().BeNull();
        Run("false and 1 / 0 = 1").Note.Should().BeNull();
    }

    [Theory]
    [InlineData("concat(\"a\", \"b\", 1)", "'ab1'")]
    [InlineData("concat(\"a\", null, \"b\")", "'ab'")]
    [InlineData("concat(null, null)", "null")]
    [InlineData("concat(\"x\", true)", "'xtrue'")]
    [InlineData("concat(\"n\", 1.5)", "'n1.5'")]
    [InlineData("concat(\"d=\", date(\"2026-01-02\"))", "'d=2026-01-02'")]
    [InlineData("concat(\"\", \"\")", "null")]
    [InlineData("concat(\"a\", \"\", \"b\")", "'ab'")]
    [InlineData("concat(\"a\", \"   \", \"b\")", "'ab'")]
    [InlineData("concat(\"\", \"  \")", "null")]
    [InlineData("concat(\"\", null)", "null")]
    [InlineData("concat(\" a \", \"b\")", "' a b'")]
    [InlineData("concat(\"n=\", 2 * 1.50)", "'n=3.00'")]
    [InlineData("concat(\"\", 1.50)", "'1.50'")]
    [InlineData("upper(\"abc\")", "'ABC'")]
    [InlineData("lower(\"ABC\")", "'abc'")]
    [InlineData("upper(null)", "null")]
    [InlineData("trim(\"  a b  \")", "'a b'")]
    [InlineData("left(\"hello\", 2)", "'he'")]
    [InlineData("right(\"hello\", 2)", "'lo'")]
    [InlineData("left(\"hi\", 5)", "'hi'")]
    [InlineData("right(\"hi\", 0)", "''")]
    [InlineData("len(\"hello\")", "5")]
    [InlineData("len(\"\")", "0")]
    [InlineData("len(null)", "null")]
    [InlineData("replace(\"a-b-c\", \"-\", \"+\")", "'a+b+c'")]
    [InlineData("replace(\"aaa\", \"a\", \"\")", "''")]
    [InlineData("contains(\"hello\", \"ell\")", "true")]
    [InlineData("contains(\"Hello\", \"hello\")", "false")]
    [InlineData("startsWith(\"hello\", \"he\")", "true")]
    [InlineData("startsWith(\"hello\", \"lo\")", "false")]
    [InlineData("startsWith(null, \"a\")", "null")]
    public void Text_functions(string source, string expected) => Shown(source).Should().Be(expected, source);

    private static FieldDefinition ConcatField(params string[] args) => new()
    {
        Name = "Out",
        DisplayName = "Out",
        Type = "string",
        Computed = new ComputedDefinition { Fn = "concat", Args = args.ToList() },
    };

    private static string ConcatOfExpression(string source, Dictionary<string, object> entry)
    {
        NameResolver types = field => entry.TryGetValue(field, out var v) ? ExpressionTypeOf(v) : null;
        var parsed = ExpressionEngine.ParseAndCheck(source, types, false);
        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors));
        var result = ExpressionEngine.Evaluate(parsed.Expression!, name => entry.TryGetValue(name.Name, out var v) ? v : null, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start));
        return (string)result.Value!;
    }

    private static ExprType? ExpressionTypeOf(object value) => value switch
    {
        string => ExprType.Text,
        bool => ExprType.Bool,
        DateTime => ExprType.Date,
        _ => ExprType.Number,
    };

    [Fact]
    public void Concat_prints_a_stored_entry_as_the_computed_field_does()
    {
        // An entry holds a date as its ISO text, which the computed field prints as it is. The
        // formula is given that day as a date value, which prints as yyyy-MM-dd with no time of day.
        var entry = new Dictionary<string, object> { ["Name"] = "Ada", ["Born"] = "2026-10-04", ["Qty"] = 1.50m, ["Active"] = true };

        var field = ComputedFields.Compute(ConcatField("Name", "Born", "Qty", "Active"), entry);
        var asValues = new Dictionary<string, object>(entry)
        {
            ["Born"] = DateTime.ParseExact((string)entry["Born"], "yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var formula = ConcatOfExpression("concat(Name, Born, Qty, Active)", asValues);

        field.Should().Be("Ada2026-10-041.50true");
        formula.Should().Be(field as string);
    }

    [Fact]
    public void Concat_reads_a_double_as_a_decimal_where_the_computed_field_prints_it_as_it_is()
    {
        var entry = new Dictionary<string, object> { ["Sum"] = 0.1d + 0.2d };

        ComputedFields.Compute(ConcatField("Sum"), entry).Should().Be("0.30000000000000004");
        ConcatOfExpression("concat(Sum, \"\")", entry).Should().Be("0.3", "concat takes two parts at least, and an empty one adds nothing");
    }

    [Theory]
    [InlineData("left(\"hello\", -1)")]
    [InlineData("left(\"hello\", 1.5)")]
    [InlineData("right(\"hello\", -2)")]
    [InlineData("replace(\"abc\", \"\", \"x\")")]
    [InlineData("addDays(date(\"2026-01-01\"), 1.5)")]
    [InlineData("addMonths(date(\"2026-01-01\"), 0.5)")]
    public void Bad_arguments_give_null_and_a_range_note(string source)
    {
        var result = Run(source);
        result.Value.Should().BeNull(source);
        result.Note.Should().NotBeNull();
        result.Note!.Rule.Should().Be(R.ArgumentRange);
    }

    [Theory]
    [InlineData("years(date(\"2000-03-15\"), date(\"2026-03-14\"))", "25")]
    [InlineData("years(date(\"2000-03-15\"), date(\"2026-03-15\"))", "26")]
    [InlineData("years(date(\"2000-03-15\"), date(\"2026-03-16\"))", "26")]
    [InlineData("years(date(\"2026-03-15\"), date(\"2000-03-15\"))", "-26")]
    [InlineData("years(date(\"2000-03-15\"), today())", "26")]
    [InlineData("years(date(\"2000-12-01\"), today())", "25")]
    [InlineData("months(date(\"2026-01-15\"), date(\"2026-03-14\"))", "1")]
    [InlineData("months(date(\"2026-01-15\"), date(\"2026-03-15\"))", "2")]
    [InlineData("months(date(\"2026-03-15\"), date(\"2026-01-15\"))", "-2")]
    [InlineData("days(date(\"2026-01-01\"), date(\"2026-01-11\"))", "10")]
    [InlineData("days(date(\"2026-01-11\"), date(\"2026-01-01\"))", "-10")]
    [InlineData("hours(date(\"2026-01-01\"), date(\"2026-01-02\"))", "24")]
    [InlineData("year(date(\"2026-03-05\"))", "2026")]
    [InlineData("month(date(\"2026-03-05\"))", "3")]
    [InlineData("day(date(\"2026-03-05\"))", "5")]
    [InlineData("year(null)", "null")]
    [InlineData("years(null, today())", "null")]
    public void Date_functions(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Theory]
    [InlineData("2025-02-27", "24")]
    [InlineData("2025-02-28", "25")]
    [InlineData("2025-03-01", "25")]
    [InlineData("2028-02-28", "27")]
    [InlineData("2028-02-29", "28")]
    [InlineData("2000-02-28", "0")]
    [InlineData("2001-02-28", "1")]
    public void Years_from_29_February_2000(string until, string expected) =>
        Shown($"years(date(\"2000-02-29\"), date(\"{until}\"))").Should().Be(expected, until);

    [Theory]
    [InlineData("2026-01-31", "2026-02-28", "1")]
    [InlineData("2026-01-31", "2026-02-27", "0")]
    [InlineData("2024-01-31", "2024-02-29", "1")]
    [InlineData("2024-02-29", "2025-02-28", "12")]
    [InlineData("2024-02-29", "2025-02-27", "11")]
    public void Months_use_the_last_day_of_a_short_month(string from, string to, string expected) =>
        Shown($"months(date(\"{from}\"), date(\"{to}\"))").Should().Be(expected);

    [Theory]
    [InlineData("addDays(date(\"2026-01-30\"), 3)", "2026-02-02")]
    [InlineData("addDays(date(\"2026-03-01\"), -1)", "2026-02-28")]
    [InlineData("addMonths(date(\"2026-01-31\"), 1)", "2026-02-28")]
    [InlineData("addMonths(date(\"2024-02-29\"), 12)", "2025-02-28")]
    [InlineData("addMonths(date(\"2026-03-15\"), -3)", "2025-12-15")]
    [InlineData("addDays(null, 1)", "null")]
    [InlineData("today()", "2026-11-27")]
    [InlineData("now()", "2026-11-27 10:30:00")]
    [InlineData("addDays(today(), 1)", "2026-11-28")]
    public void Date_arithmetic_and_the_clock(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Fact]
    public void The_clock_comes_from_the_provider_it_is_given()
    {
        var later = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
        Show(Run("today()", clock: later).Value).Should().Be("2030-01-02");
        Show(Run("now()", clock: later).Value).Should().Be("2030-01-02 03:04:05");

        later.Advance(TimeSpan.FromDays(1));
        Show(Run("today()", clock: later).Value).Should().Be("2030-01-03");
    }

    [Theory]
    [InlineData("format(1234.5, \"#,##0.00\")", "'1,234.50'")]
    [InlineData("format(1234567, \"#,##0\")", "'1,234,567'")]
    [InlineData("format(1234.5, \"0\")", "'1235'")]
    [InlineData("format(2.5, \"0\")", "'3'")]
    [InlineData("format(-2.5, \"0\")", "'-3'")]
    [InlineData("format(1.5, \"0.0\")", "'1.5'")]
    [InlineData("format(1.5, \"0.00\")", "'1.50'")]
    [InlineData("format(1.5, \"0.000\")", "'1.500'")]
    [InlineData("format(0.5, \"0%\")", "'50%'")]
    [InlineData("format(0.256, \"0.0%\")", "'25.6%'")]
    [InlineData("format(-0.001, \"0.00\")", "'0.00'")]
    [InlineData("format(null, \"0\")", "null")]
    [InlineData("format(date(\"2026-03-05\"), \"yyyy-MM-dd\")", "'2026-03-05'")]
    [InlineData("format(date(\"2026-03-05\"), \"dd/MM/yyyy\")", "'05/03/2026'")]
    [InlineData("format(date(\"2026-03-05\"), \"MM/dd/yyyy\")", "'03/05/2026'")]
    [InlineData("format(date(\"2026-03-05\"), \"d MMM yyyy\")", "'5 Mar 2026'")]
    [InlineData("format(date(\"2026-03-05\"), \"MMMM d, yyyy\")", "'March 5, 2026'")]
    [InlineData("format(date(\"2026-03-05\"), \"MMM yyyy\")", "'Mar 2026'")]
    [InlineData("format(date(\"2026-03-05\"), \"yyyy\")", "'2026'")]
    [InlineData("format(now(), \"yyyy-MM-dd HH:mm\")", "'2026-11-27 10:30'")]
    [InlineData("format(now(), \"HH:mm\")", "'10:30'")]
    public void Format_patterns(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Fact]
    public void The_function_table_is_exactly_the_documented_list()
    {
        string[] documented =
        [
            "round", "floor", "ceiling", "abs", "min", "max", "iif", "switch", "coalesce", "isblank", "concat", "upper", "lower",
            "trim", "left", "right", "len", "replace", "contains", "startsWith", "years", "months", "days", "hours", "addDays",
            "addMonths", "year", "month", "day", "today", "now", "format",
        ];

        FunctionTable.Names.Should().BeEquivalentTo(documented, "a function added or dropped needs this list, the docs and its tests changed together");
    }

    [Fact]
    public void Function_names_are_not_case_sensitive()
    {
        Shown("UPPER(\"a\")").Should().Be("'A'");
        Shown("StartsWith(\"ab\", \"a\")").Should().Be("true");
    }

    [Fact]
    public void Overflow_and_division_by_zero_give_null_and_a_note()
    {
        var overflow = Run("79228162514264337593543950335 * 2");
        overflow.Value.Should().BeNull();
        overflow.Note!.Rule.Should().Be(R.Overflow);

        var zero = Run("1 / 0");
        zero.Value.Should().BeNull();
        zero.Note!.Rule.Should().Be(R.DivisionByZero);

        Run("addMonths(date(\"2026-01-01\"), 100000000)").Note!.Rule.Should().Be(R.Overflow);
        Run("addDays(date(\"9999-12-31\"), 1)").Note!.Rule.Should().Be(R.Overflow);
        Run("format(79228162514264337593543950335, \"0%\")").Note!.Rule.Should().Be(R.Overflow);
    }

    [Fact]
    public void Text_that_grows_past_the_bound_stops_the_whole_formula()
    {
        var limits = ExpressionLimits.Default with { MaxText = 20 };
        var result = Run("coalesce(concat(\"aaaaaaaaaaaaaaaaaaaaaaaaa\", \"b\"), \"fallback\")", limits: limits);
        result.Value.Should().BeNull("a bound hit is not a value that coalesce may replace");
        result.Note!.Rule.Should().Be(R.TextTooLong);
    }

    [Fact]
    public void Numbers_and_dates_come_out_the_same_under_a_comma_decimal_culture()
    {
        string[] sources =
        [
            "concat(\"n\", 1.5)", "format(1234.5, \"#,##0.00\")", "format(0.256, \"0.0%\")", "round(2.5) + 0.25",
            "format(date(\"2026-03-05\"), \"MMMM d, yyyy\")", "concat(\"d=\", now())", "1.5 * 2 = 3", "format(date(\"2026-03-05\"), \"dd/MM/yyyy\")",
        ];
        var baseline = sources.Select(s => Shown(s)).ToArray();
        baseline[0].Should().Be("'n1.5'");

        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            var comma = CommaCulture();
            comma.NumberFormat.NumberDecimalSeparator.Should().Be(",");
            CultureInfo.CurrentCulture = comma;
            CultureInfo.CurrentUICulture = comma;

            sources.Select(s => Shown(s)).Should().Equal(baseline);
            Run("1.5 + 1").Value.Should().Be(2.5m);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
    }

    private static CultureInfo CommaCulture()
    {
        try
        {
            var german = new CultureInfo("de-DE");
            if (german.NumberFormat.NumberDecimalSeparator == ",")
            {
                return german;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        var clone = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        clone.NumberFormat.NumberDecimalSeparator = ",";
        clone.NumberFormat.NumberGroupSeparator = ".";
        clone.NumberFormat.PercentDecimalSeparator = ",";
        clone.DateTimeFormat.DateSeparator = ".";
        clone.DateTimeFormat.TimeSeparator = ".";
        return clone;
    }
}
