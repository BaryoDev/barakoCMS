using barakoCMS.Core.Expressions;
using R = barakoCMS.Core.Expressions.Rules;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static BarakoCMS.Tests.ExpressionKit;

namespace BarakoCMS.Tests;

/// <summary>Grammar, precedence, null rules, the type check, and every bound of the formula language.</summary>
public class ExpressionParserTests
{
    private static ExpressionError Refused(string source, bool refuseClock = false, ExpressionLimits? limits = null)
    {
        var parsed = Check(source, refuseClock, limits);
        parsed.IsValid.Should().BeFalse($"'{Trim(source)}' should be refused");
        parsed.Errors.Should().NotBeEmpty();
        return parsed.Errors[0];
    }

    private static string Trim(string source) => source.Length > 60 ? source[..60] + "..." : source;

    private static string Nested(int parens) => new string('(', parens) + "1" + new string(')', parens);

    private static ExpressionLimits Wide => ExpressionLimits.Default with { MaxSource = 200_000 };

    [Theory]
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("10 - 2 - 3", "5")]
    [InlineData("8 / 4 / 2", "1")]
    [InlineData("-2 * 3", "-6")]
    [InlineData("--2", "2")]
    [InlineData("1 - -1", "2")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("2 * 3 = 6", "true")]
    [InlineData("1 + 1 != 3", "true")]
    [InlineData("1 <= 1", "true")]
    [InlineData("2 >= 3", "false")]
    [InlineData("not true or true", "true")]
    [InlineData("not 1 = 2", "true")]
    [InlineData("true or false and false", "true")]
    [InlineData("(true or false) and false", "false")]
    [InlineData("1 = 1 and 2 = 2", "true")]
    [InlineData("TRUE AND NOT FALSE", "true")]
    [InlineData("\"a\" = \"a\"", "true")]
    [InlineData("\"a\" < \"b\"", "true")]
    [InlineData("\"B\" < \"a\"", "true")]
    [InlineData("date(\"2026-01-01\") < date(\"2026-01-02\")", "true")]
    [InlineData("\"say \\\"hi\\\" \\\\ now\"", "'say \"hi\" \\ now'")]
    [InlineData("  1\t+\n2  ", "3")]
    [InlineData("null", "null")]
    public void Grammar_and_precedence(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Theory]
    [InlineData("null = null", "true")]
    [InlineData("1 = null", "false")]
    [InlineData("null != 1", "true")]
    [InlineData("Qty = null", "true")]
    [InlineData("1 + null", "null")]
    [InlineData("null * 2", "null")]
    [InlineData("-null", "null")]
    [InlineData("null < 1", "null")]
    [InlineData("1 >= null", "null")]
    [InlineData("null and false", "false")]
    [InlineData("false and null", "false")]
    [InlineData("null and true", "null")]
    [InlineData("null or true", "true")]
    [InlineData("true or null", "true")]
    [InlineData("null or false", "null")]
    [InlineData("not null", "null")]
    [InlineData("Qty * 2", "null")]
    public void Null_rules(string source, string expected) => Shown(source).Should().Be(expected, source);

    [Fact]
    public void Names_read_through_the_value_resolver_with_or_without_data_prefix()
    {
        var values = new Dictionary<string, object?> { ["Qty"] = 3m, ["Price"] = 2.5m, ["Name"] = "Ada", ["Active"] = true };

        Shown("Qty * Price", values).Should().Be("7.5");
        Shown("data.Qty * data.Price", values).Should().Be("7.5");
        Shown("DATA.Qty", values).Should().Be("3");
        Shown("concat(Name, \"!\")", values).Should().Be("'Ada!'");
        Shown("iif(Active, \"on\", \"off\")", values).Should().Be("'on'");
    }

    [Fact]
    public void Whole_numbers_doubles_and_dates_from_the_resolver_become_the_five_types()
    {
        var values = new Dictionary<string, object?>
        {
            ["Qty"] = 4,
            ["Price"] = 1.25d,
            ["Born"] = new DateTimeOffset(2000, 3, 15, 22, 0, 0, TimeSpan.FromHours(8)),
        };

        Shown("Qty * Price", values).Should().Be("5");
        Shown("year(Born)", values).Should().Be("2000");
    }

    [Fact]
    public void A_stored_value_of_the_wrong_type_gives_null_and_a_note_that_does_not_quote_it()
    {
        var result = Run("Qty + 1", new Dictionary<string, object?> { ["Qty"] = "SECRET-VALUE" });

        result.Value.Should().BeNull();
        result.Note!.Rule.Should().Be(R.ValueType);
        result.Note.ToString().Should().NotContain("SECRET-VALUE");
    }

    [Fact]
    public void System_names_are_known_without_the_resolver_and_asked_about_nothing()
    {
        var asked = new List<string>();
        var parsed = ExpressionEngine.ParseAndCheck("concat(id, status) = \"x\" and year(createdAt) > 2000", f =>
        {
            asked.Add(f);
            return null;
        });

        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors));
        asked.Should().BeEmpty();
        parsed.Expression!.Fields.Should().BeEmpty("system names are not fields of the type");

        var values = new Dictionary<string, object?> { ["id"] = "abc", ["status"] = "open", ["createdAt"] = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero) };
        Shown("concat(ID, Status)", values).Should().Be("'abcopen'");
        Shown("year(CREATEDAT)", values).Should().Be("2026");
    }

    [Fact]
    public void The_checked_expression_lists_the_fields_it_reads_once_each()
    {
        var expression = Compile("Qty * Price + data.Qty - len(Name)");

        expression.Fields.Should().Equal("Qty", "Price", "Name");
        expression.Type.Should().Be(ExprType.Number);
        expression.UsesClock.Should().BeFalse();
        expression.NodeCount.Should().BeGreaterThan(5);
    }

    [Theory]
    [InlineData("Qty * 2", "Number")]
    [InlineData("Name", "Text")]
    [InlineData("Born", "Date")]
    [InlineData("Qty > 1", "Bool")]
    [InlineData("null", "Null")]
    [InlineData("iif(Active, Qty, 0)", "Number")]
    [InlineData("coalesce(Note, Name)", "Text")]
    [InlineData("format(Qty, \"0.00\")", "Text")]
    [InlineData("addDays(Born, 1)", "Date")]
    public void The_result_type_is_known_before_any_value_is(string source, string expected) =>
        Compile(source).Type.ToString().Should().Be(expected);

    [Fact]
    public void A_number_times_text_is_refused_where_the_operator_is()
    {
        var error = Refused("Qty * Name");

        error.Rule.Should().Be(R.TypeMismatch);
        error.Position.Should().Be(4);
    }

    [Theory]
    [InlineData("Name + 1")]
    [InlineData("Qty = Name")]
    [InlineData("Qty < Born")]
    [InlineData("Active < Active")]
    [InlineData("1 and 2")]
    [InlineData("not 1")]
    [InlineData("-\"a\"")]
    [InlineData("iif(1, 2, 3)")]
    [InlineData("iif(true, 1, \"a\")")]
    [InlineData("coalesce(Qty, Name)")]
    [InlineData("switch(true, 1, \"a\")")]
    [InlineData("upper(1)")]
    [InlineData("years(1, 2)")]
    [InlineData("left(\"a\", \"b\")")]
    [InlineData("addDays(Born, Name)")]
    [InlineData("format(Name, \"0\")")]
    public void Operands_that_do_not_fit_are_a_type_mismatch(string source) =>
        Refused(source).Rule.Should().Be(R.TypeMismatch, source);

    [Fact]
    public void Every_problem_is_reported_not_only_the_first()
    {
        var parsed = Check("Missing1 + Missing2 * Name");

        parsed.Errors.Select(e => e.Rule).Should().Contain(R.UnknownName);
        parsed.Errors.Count(e => e.Rule == R.UnknownName).Should().Be(2);
    }

    [Theory]
    [InlineData("Missing + 1", R.UnknownName, 0)]
    [InlineData("1 + data.Missing", R.UnknownName, 4)]
    [InlineData("x.y", R.MemberAccess, 1)]
    [InlineData("Qty.Name", R.MemberAccess, 3)]
    [InlineData("sqrt(4)", R.UnknownFunction, 0)]
    [InlineData("1 + eval(\"1\")", R.UnknownFunction, 4)]
    [InlineData("round()", R.Arity, 0)]
    [InlineData("round(1, 2, 3)", R.Arity, 0)]
    [InlineData("iif(true, 1)", R.Arity, 0)]
    [InlineData("switch(true, 1, false, 2)", R.Arity, 0)]
    [InlineData("today(1)", R.Arity, 0)]
    [InlineData("round(1.5, 11)", R.ArgumentRange, 11)]
    [InlineData("round(1.5, 2.5)", R.ArgumentRange, 11)]
    [InlineData("format(1, \"0.0000\")", R.FormatPattern, 10)]
    [InlineData("format(date(\"2026-01-01\"), \"0\")", R.FormatPattern, 27)]
    [InlineData("format(1, \"yyyy\")", R.FormatPattern, 10)]
    [InlineData("format(Qty, Name)", R.FormatPattern, 12)]
    public void Names_functions_arity_and_patterns_are_checked(string source, string rule, int position)
    {
        var error = Refused(source);

        error.Rule.Should().Be(rule, source);
        error.Position.Should().Be(position, source);
    }

    [Theory]
    [InlineData("", R.Empty, 0)]
    [InlineData("   ", R.Empty, 0)]
    [InlineData("1 + $", R.UnexpectedCharacter, 4)]
    [InlineData("1 +", R.Syntax, 3)]
    [InlineData("(1", R.Syntax, 2)]
    [InlineData("1)", R.Syntax, 1)]
    [InlineData("1 2", R.Syntax, 2)]
    [InlineData("1 < 2 < 3", R.Syntax, 6)]
    [InlineData("and", R.Syntax, 0)]
    [InlineData("1 and", R.Syntax, 5)]
    [InlineData("round(1,)", R.Syntax, 8)]
    [InlineData("data.", R.Syntax, 5)]
    [InlineData("1e5", R.Exponent, 1)]
    [InlineData("1.5E2", R.Exponent, 3)]
    [InlineData("1.", R.BadNumber, 1)]
    [InlineData("12abc", R.BadNumber, 2)]
    [InlineData("99999999999999999999999999999999", R.BadNumber, 0)]
    [InlineData("\"abc", R.BadString, 0)]
    [InlineData("\"a\\nb\"", R.BadString, 2)]
    [InlineData("date(\"2026-02-30\")", R.BadDate, 5)]
    [InlineData("date(\"2026-1-1\")", R.BadDate, 5)]
    [InlineData("date(1)", R.BadDate, 5)]
    [InlineData("a == b", R.Syntax, 3)]
    [InlineData("Qty & 1", R.UnexpectedCharacter, 4)]
    [InlineData("1 ! 2", R.UnexpectedCharacter, 2)]
    public void Syntax_errors_name_the_rule_and_the_position(string source, string rule, int position)
    {
        var error = Refused(source);

        error.Rule.Should().Be(rule, source);
        error.Position.Should().Be(position, source);
    }

    [Fact]
    public void A_missing_source_is_an_empty_formula()
    {
        ExpressionEngine.ParseAndCheck(null, ExpressionKit.Resolve).Errors[0].Rule.Should().Be(R.Empty);
    }

    [Fact]
    public void The_clock_functions_can_be_refused()
    {
        Check("today()").IsValid.Should().BeTrue();
        Check("now()").IsValid.Should().BeTrue();
        Compile("years(Born, today())").UsesClock.Should().BeTrue();

        Refused("years(Born, today())", refuseClock: true).Rule.Should().Be(R.ClockFunction);
        Refused("format(now(), \"HH:mm\")", refuseClock: true).Rule.Should().Be(R.ClockFunction);
        Check("years(Born, date(\"2026-01-01\"))", refuseClock: true).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_source_over_the_length_bound_is_refused_before_it_is_read()
    {
        Check("1" + new string(' ', 1999)).IsValid.Should().BeTrue("2000 characters is the bound");
        Refused("1" + new string(' ', 2000)).Rule.Should().Be(R.SourceTooLong);
        Refused(new string('1', 1_000_000)).Rule.Should().Be(R.SourceTooLong);
        Refused("\"" + new string('a', 1_000_000) + "\"").Rule.Should().Be(R.SourceTooLong);
    }

    [Fact]
    public void Deep_nesting_is_refused_without_a_stack_overflow()
    {
        Check(Nested(31)).IsValid.Should().BeTrue("31 brackets and the formula itself make 32 levels");
        Refused(Nested(32)).Rule.Should().Be(R.DepthLimit);

        Refused(Nested(10_000), limits: Wide).Rule.Should().Be(R.DepthLimit);
        Refused(Nested(100_000), limits: ExpressionLimits.Default with { MaxSource = 1_000_000 }).Rule.Should().Be(R.DepthLimit);
        Refused(string.Concat(Enumerable.Repeat("not ", 40)) + "true").Rule.Should().Be(R.DepthLimit);
        Refused(new string('-', 50) + "1").Rule.Should().Be(R.DepthLimit);
        Refused(string.Concat(Enumerable.Repeat("iif(true,", 40)) + "1" + string.Concat(Enumerable.Repeat(",1)", 40))).Rule.Should().Be(R.DepthLimit);
    }

    [Fact]
    public void A_flat_chain_is_a_deep_tree_and_is_held_to_the_same_depth()
    {
        string Chain(int operands) => string.Join(" + ", Enumerable.Repeat("1", operands));

        Check(Chain(32)).IsValid.Should().BeTrue();
        Refused(Chain(33)).Rule.Should().Be(R.DepthLimit);
    }

    [Fact]
    public void More_than_the_node_bound_is_refused()
    {
        string Many(int args) => "concat(" + string.Join(",", Enumerable.Repeat("1", args)) + ")";

        Check(Many(499)).IsValid.Should().BeTrue("499 arguments and the call make 500 nodes");
        Refused(Many(500)).Rule.Should().Be(R.NodeLimit);
    }

    [Fact]
    public void A_step_budget_smaller_than_the_formula_is_refused_at_parse_time()
    {
        var tight = ExpressionLimits.Default with { MaxSteps = 10 };

        Check("1 + 2", limits: tight).IsValid.Should().BeTrue();
        Refused("1+1+1+1+1+1+1+1", limits: tight).Rule.Should().Be(R.StepBudget);
    }

    [Fact]
    public void The_bounds_are_checked_again_at_evaluate_time_against_the_limits_given_then()
    {
        var expression = Compile("1 + 2 + 3 + 4");
        var values = new ValueResolver(_ => null);

        ExpressionEngine.Evaluate(expression, values).Value.Should().Be(10m);

        var tighter = new (ExpressionLimits Limits, string Rule)[]
        {
            (ExpressionLimits.Default with { MaxSource = 5 }, R.SourceTooLong),
            (ExpressionLimits.Default with { MaxNodes = 5 }, R.NodeLimit),
            (ExpressionLimits.Default with { MaxDepth = 3 }, R.DepthLimit),
            (ExpressionLimits.Default with { MaxSteps = 4 }, R.StepBudget),
        };
        foreach (var (limits, rule) in tighter)
        {
            var result = ExpressionEngine.Evaluate(expression, values, new FakeTimeProvider(Start), limits);
            result.Value.Should().BeNull(rule);
            result.Note!.Rule.Should().Be(rule);
        }
    }

    [Fact]
    public void Work_on_long_text_runs_down_the_step_budget()
    {
        var limits = Wide with { MaxSteps = 100 };
        var result = Run($"len(upper(\"{new string('a', 5000)}\"))", limits: limits);

        result.Value.Should().BeNull();
        result.Note!.Rule.Should().Be(R.StepBudget);

        Run($"len(upper(\"{new string('a', 5000)}\"))", limits: Wide).Value.Should().Be(5000m);
    }

    [Fact]
    public void Comparing_long_text_is_charged_too()
    {
        var limits = Wide with { MaxSteps = 100 };
        var text = new string('a', 4000);

        var result = Run($"\"{text}\" = \"{text}\"", limits: limits);

        result.Value.Should().BeNull();
        result.Note!.Rule.Should().Be(R.StepBudget);
    }

    [Fact]
    public void A_text_result_over_the_length_bound_gives_null_and_a_note()
    {
        var limits = Wide with { MaxText = 10_000 };
        var piece = new string('a', 6000);

        var result = Run($"concat(\"{piece}\", \"{piece}\")", limits: limits);

        result.Value.Should().BeNull();
        result.Note!.Rule.Should().Be(R.TextTooLong);
        Run($"len(concat(\"{piece}\", \"b\"))", limits: limits).Value.Should().Be(6001m);
    }

    [Fact]
    public void Replace_that_would_grow_past_the_bound_is_stopped_before_it_builds_the_text()
    {
        var limits = Wide;
        var text = new string('a', 9000);
        var result = Run($"replace(\"{text}\", \"a\", \"bbbbbbbbbb\")", limits: limits);

        result.Value.Should().BeNull();
        result.Note!.Rule.Should().Be(R.TextTooLong);
    }

    [Theory]
    [InlineData("\"SECRET-VALUE\" + 1")]
    [InlineData("Qty = \"SECRET-VALUE\"")]
    [InlineData("date(\"SECRET-VALUE\")")]
    [InlineData("\"SECRET-VALUE")]
    [InlineData("\"SECRET-VALUE\" \"x\"")]
    [InlineData("format(1, \"SECRET-VALUE\")")]
    [InlineData("upper(\"SECRET-VALUE\", 1)")]
    [InlineData("iif(\"SECRET-VALUE\", 1, 2)")]
    [InlineData("left(\"SECRET-VALUE\", \"SECRET-VALUE\")")]
    public void Errors_never_quote_the_text_of_a_string_literal(string source)
    {
        var parsed = Check(source);

        parsed.IsValid.Should().BeFalse(source);
        parsed.Errors.Should().OnlyContain(e => !e.Message.Contains("SECRET-VALUE") && !e.ToString().Contains("SECRET-VALUE"));
    }

    [Fact]
    public void Parsing_the_same_text_twice_gives_the_same_answer()
    {
        var a = Check("Qty * (Price + 1)");
        var b = Check("Qty * (Price + 1)");

        a.IsValid.Should().BeTrue();
        a.Expression!.NodeCount.Should().Be(b.Expression!.NodeCount);
        Check("Qty *").Errors.Should().BeEquivalentTo(Check("Qty *").Errors);
    }

    [Fact]
    public void No_formula_text_can_name_a_type_or_a_member_outside_the_table()
    {
        foreach (var source in new[] { "System.Environment.Exit(1)", "Name.Length", "GetType()", "typeof(int)", "new Object()", "Name.ToUpper()", "data.Name.Length" })
        {
            Check(source).IsValid.Should().BeFalse(source);
        }
    }
}
