using System.Diagnostics;
using barakoCMS.Core.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static BarakoCMS.Tests.ExpressionKit;
using R = barakoCMS.Core.Expressions.Rules;

namespace BarakoCMS.Tests;

/// <summary>
/// 10,000 inputs from one seed: half random, half mutated from valid formulas. Each must parse, or
/// give a parse error, with no other exception and in under 50 ms. Set EXPRESSION_FUZZ_SEED to replay
/// a failure; the failure message prints the seed.
/// </summary>
/// <remarks>
/// A shared runner can stall for a garbage collection or the scheduler, so an input over the bound is
/// timed again, up to three runs, and fails only when every run is over. A slow path is slow every time.
/// </remarks>
public class ExpressionFuzzTests
{
    private const int Count = 10_000;
    private const int FixedSeed = 1420;
    private const int Tries = 3;
    private static readonly TimeSpan Slowest = TimeSpan.FromMilliseconds(50);

    private static readonly string[] Corpus =
    [
        "Qty * Price",
        "Qty * (Price + 1) - 2 / 4",
        "round(Qty * Price, 2)",
        "iif(Qty > 10, \"bulk\", \"single\")",
        "switch(Qty < 1, \"none\", Qty < 10, \"few\", \"many\")",
        "coalesce(Note, Name, \"unknown\")",
        "concat(upper(Name), \" - \", format(Price, \"#,##0.00\"))",
        "years(Born, today()) >= 18 and not isblank(Name)",
        "format(addMonths(Born, 6), \"d MMM yyyy\")",
        "left(trim(Name), 3) = \"Ada\" or contains(Note, \"x\")",
        "replace(Name, \"a\", \"b\")",
        "min(Qty, Price, 3) + max(1, 2) + abs(-1) + floor(1.5) + ceiling(1.5)",
        "days(Born, date(\"2026-01-01\")) / 7",
        "data.Qty + data.Price",
        "id = \"x\" and status != \"closed\" and year(createdAt) > 2000",
        "\"say \\\"hi\\\" \\\\ now\"",
        "-(-(Qty))",
    ];

    private static readonly string[] Pieces =
    [
        "(", ")", ",", ".", "+", "-", "*", "/", "=", "!=", "<", "<=", ">", ">=", "and", "or", "not", "true", "false", "null",
        "Qty", "Name", "Born", "data.", "id", "status", "1", "0", "0.5", "123456789012345678901234567890", "1e5", "\"x\"",
        "\"", "\\", "date(", "round(", "iif(", "switch(", "concat(", "format(", "years(", "today()", "now()", "sqrt(", " ",
        "\t", "\n", "é", "\0", "\u202e", "\ud83d\ude00", "\ud800", "$", "@", "{", "}", "[", "]", ";", "'", "#", "&", "|", "!",
    ];

    private static int SeedToUse()
    {
        var text = Environment.GetEnvironmentVariable("EXPRESSION_FUZZ_SEED");
        return int.TryParse(text, out var seed) ? seed : FixedSeed;
    }

    internal static List<string> Inputs(int seed, int count)
    {
        var random = new Random(seed);
        var inputs = new List<string>(count);
        for (var i = 0; i < count / 2; i++)
        {
            inputs.Add(RandomInput(random));
        }

        while (inputs.Count < count)
        {
            inputs.Add(Mutate(Corpus[random.Next(Corpus.Length)], random));
        }

        return inputs;
    }

    private static string RandomInput(Random random)
    {
        switch (random.Next(5))
        {
            case 0:
                var chars = new char[random.Next(0, 80)];
                for (var i = 0; i < chars.Length; i++)
                {
                    chars[i] = (char)random.Next(0, random.Next(2) == 0 ? 128 : 65536);
                }

                return new string(chars);
            case 1:
                return string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => Pieces[random.Next(Pieces.Length)]));
            case 2:
                var depth = random.Next(1, 3000);
                return new string("(-n".Substring(random.Next(3), 1)[0], depth) + "1" + new string(')', random.Next(0, depth + 1));
            case 3:
                return string.Concat(Enumerable.Range(0, random.Next(1, 600)).Select(_ => Pieces[random.Next(14)]));
            default:
                return "\"" + new string((char)random.Next(32, 127), random.Next(0, 4000)) + (random.Next(2) == 0 ? "\"" : string.Empty);
        }
    }

    private static string Mutate(string valid, Random random)
    {
        var text = valid;
        for (var rounds = random.Next(1, 5); rounds > 0; rounds--)
        {
            var at = random.Next(0, text.Length + 1);
            switch (random.Next(7))
            {
                case 0 when text.Length > 0:
                    text = text.Remove(Math.Min(at, text.Length - 1), 1);
                    break;
                case 1:
                    text = text.Insert(at, Pieces[random.Next(Pieces.Length)]);
                    break;
                case 2 when text.Length > 0:
                    text = text[..Math.Min(at, text.Length)];
                    break;
                case 3 when text.Length > 0:
                    var from = random.Next(text.Length);
                    text = text.Insert(at, text.Substring(from, random.Next(1, text.Length - from + 1)));
                    break;
                case 4 when text.Length > 1:
                    var a = random.Next(text.Length);
                    var b = random.Next(text.Length);
                    var swapped = text.ToCharArray();
                    (swapped[a], swapped[b]) = (swapped[b], swapped[a]);
                    text = new string(swapped);
                    break;
                case 5:
                    text = string.Concat(Enumerable.Repeat(text, random.Next(2, 6)));
                    break;
                default:
                    text = "(" + text + ")";
                    break;
            }
        }

        return text;
    }

    [Fact]
    public void The_same_seed_gives_the_same_inputs()
    {
        Inputs(7, 500).Should().Equal(Inputs(7, 500));
        Inputs(7, 500).Should().NotEqual(Inputs(8, 500));
    }

    [Fact]
    public void The_corpus_is_valid_so_the_mutations_start_from_something_real()
    {
        foreach (var source in Corpus)
        {
            Check(source).IsValid.Should().BeTrue($"{source}: {string.Join("; ", Check(source).Errors)}");
        }
    }

    private static readonly FakeTimeProvider FuzzClock = new(Start);

    private static ValueResolver Resolver(int seed, int index)
    {
        var random = new Random(unchecked((seed * 100_003) + index));
        return name => RandomValue(random, name.IsSystem ? SystemType(name.Name) : FuzzResolve(name.Name));
    }

    // The shared schema plus a field named like the system name status, so a bare status
    // is refused as ambiguous and data.status still reads the field. Only the fuzz sees it.
    private static ExprType? FuzzResolve(string field) =>
        field.Equals("status", StringComparison.OrdinalIgnoreCase) ? ExprType.Text : Resolve(field);

    private static ParseResult FuzzCheck(string source) => ExpressionEngine.ParseAndCheck(source, FuzzResolve, false);

    [Fact]
    public void The_fuzz_schema_reaches_the_ambiguous_name_path()
    {
        FuzzCheck("status = \"x\"").Errors.Should().Contain(e => e.Rule == R.AmbiguousName);
        FuzzCheck("data.status = \"x\"").IsValid.Should().BeTrue();
        Check("status = \"x\"").IsValid.Should().BeTrue("the shared schema has no field called status");
    }

    private static ExprType? SystemType(string name) => name is "createdAt" or "updatedAt" ? ExprType.Date : ExprType.Text;

    /// <summary>A value for a field of the given type, with some values of a type it does not have.</summary>
    internal static object? RandomValue(Random random, ExprType? type)
    {
        var roll = random.Next(100);
        if (roll < 5)
        {
            return null;
        }

        if (roll < 20 || type is null)
        {
            var wrong = type is null
                ? (ExprType)random.Next(5)
                : Enum.GetValues<ExprType>().Where(t => t != type.Value).ElementAt(random.Next(Enum.GetValues<ExprType>().Length - 1));
            return RandomOf(random, wrong, wrongOnly: true);
        }

        return RandomOf(random, type.Value, wrongOnly: false);
    }

    private static object? RandomOf(Random random, ExprType type, bool wrongOnly)
    {
        if (wrongOnly && random.Next(4) == 0)
        {
            return random.Next(2) == 0 ? NextGuid(random) : TimeSpan.FromMinutes(random.Next(1000));
        }

        switch (type)
        {
            case ExprType.Number:
                object[] numbers =
                [
                    decimal.MaxValue, decimal.MinValue, 0m, 1.5m, -3m, 100000000m, 7, int.MaxValue, int.MinValue, long.MaxValue, long.MinValue,
                    1.5d, 1e300d, -1e300d, double.NaN, double.PositiveInfinity, 7.9e28d, 8e28d, 2.5f, float.MaxValue, float.MinValue, 1e29f, float.NaN,
                ];
                return numbers[random.Next(numbers.Length)];
            case ExprType.Text:
                return random.Next(4) switch
                {
                    0 => string.Empty,
                    1 => "  ",
                    2 => "Ada Lovelace",
                    _ => new string((char)('a' + random.Next(26)), random.Next(1, 20_001)),
                };
            case ExprType.Bool:
                return random.Next(2) == 0;
            case ExprType.Date:
                object[] dates =
                [
                    DateTime.MinValue, DateTime.MaxValue, new DateTime(2026, 11, 27, 10, 30, 0, DateTimeKind.Utc), DateTimeOffset.MinValue,
                    DateTimeOffset.MaxValue, new DateTimeOffset(2026, 2, 28, 23, 0, 0, TimeSpan.FromHours(8)), DateOnly.MinValue, DateOnly.MaxValue,
                    new DateOnly(2024, 2, 29),
                ];
                return dates[random.Next(dates.Length)];
            default:
                return NextGuid(random);
        }
    }

    private static Guid NextGuid(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    [Fact]
    public void Ten_thousand_inputs_parse_or_give_a_parse_error_within_50_ms()
    {
        var seed = SeedToUse();
        var inputs = Inputs(seed, Count);
        inputs.Should().HaveCount(Count);

        foreach (var warm in inputs.Take(300).Concat(Corpus))
        {
            var checkedWarm = FuzzCheck(warm);
            if (checkedWarm.IsValid)
            {
                ExpressionEngine.Evaluate(checkedWarm.Expression!, Resolver(seed, 0), FuzzClock);
            }
        }

        var failures = new List<string>();
        var parsed = 0;
        var refused = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            var source = inputs[i];
            var run = RunOne(source, Resolver(seed, i));
            parsed += run.Valid == true ? 1 : 0;
            refused += run.Valid == false ? 1 : 0;
            if (run.Problem.Length > 0)
            {
                failures.Add($"#{i} {(run.Valid == false ? "refused" : "ran")} with {run.Problem} on {Describe(source)}");
            }

            var fastest = run.Elapsed;
            for (var again = 1; again < Tries && fastest > Slowest; again++)
            {
                var retry = RunOne(source, Resolver(seed, i)).Elapsed;
                fastest = retry < fastest ? retry : fastest;
            }

            if (fastest > Slowest)
            {
                failures.Add($"#{i} took at least {(long)fastest.TotalMilliseconds} ms on each of {Tries} runs on {Describe(source)}");
            }
        }

        failures.Should().BeEmpty($"seed {seed}; replay with EXPRESSION_FUZZ_SEED={seed}");
        parsed.Should().BeGreaterThan(100, $"seed {seed}: the mutated half should keep some valid inputs");
        refused.Should().BeGreaterThan(Count / 4, $"seed {seed}: the random half should mostly be refused");
    }

    private sealed record FuzzRun(bool? Valid, string Problem, TimeSpan Elapsed);

    /// <summary>Checks one input and runs it when it parses, timing both. Valid is null when the check threw.</summary>
    private static FuzzRun RunOne(string source, ValueResolver resolver)
    {
        bool? valid = null;
        var problem = string.Empty;
        var clock = Stopwatch.StartNew();
        try
        {
            var result = FuzzCheck(source);
            valid = result.IsValid;
            if (result.IsValid)
            {
                ExpressionEngine.Evaluate(result.Expression!, resolver, FuzzClock);
            }
            else if (result.Errors.Count == 0 || !result.Errors.All(e => e.Position >= 0 && !string.IsNullOrEmpty(e.Rule)))
            {
                problem = "a refusal without a rule and a position";
            }
        }
        catch (Exception e)
        {
            problem = $"threw {e.GetType().Name}: {e.Message}";
        }

        clock.Stop();
        return new FuzzRun(valid, problem, clock.Elapsed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Other_seeds_hold_too(int seed)
    {
        var inputs = Inputs(seed, 1500);
        for (var i = 0; i < inputs.Count; i++)
        {
            var source = inputs[i];
            var resolver = Resolver(seed, i);
            var act = () =>
            {
                var result = FuzzCheck(source);
                if (result.IsValid)
                {
                    ExpressionEngine.Evaluate(result.Expression!, resolver, FuzzClock);
                }
            };

            act.Should().NotThrow($"seed {seed}, input {Describe(source)}");
        }
    }

    [Fact]
    public void The_typed_values_cover_the_edges_and_repeat_for_one_seed()
    {
        var seen = new HashSet<Type>();
        var random = new Random(5);
        for (var i = 0; i < 4000; i++)
        {
            foreach (var type in new ExprType?[] { ExprType.Number, ExprType.Text, ExprType.Bool, ExprType.Date, null })
            {
                if (RandomValue(random, type) is { } value)
                {
                    seen.Add(value.GetType());
                }
            }
        }

        seen.Should().Contain([typeof(decimal), typeof(int), typeof(long), typeof(double), typeof(float), typeof(string), typeof(DateTime),
            typeof(DateTimeOffset), typeof(DateOnly), typeof(Guid), typeof(bool), typeof(TimeSpan)]);

        var one = Resolver(9, 3);
        var two = Resolver(9, 3);
        var name = new NameRef("Qty", false);
        Enumerable.Range(0, 50).Select(_ => one(name)).Should().Equal(Enumerable.Range(0, 50).Select(_ => two(name)));
    }

    private static string Describe(string source) =>
        source.Length <= 120 ? $"\"{source.Replace("\0", "\\0")}\"" : $"a {source.Length} character input starting \"{source[..60].Replace("\0", "\\0")}\"";
}
