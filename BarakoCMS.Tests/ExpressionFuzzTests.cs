using System.Diagnostics;
using barakoCMS.Core.Expressions;
using FluentAssertions;
using Xunit;
using static BarakoCMS.Tests.ExpressionKit;

namespace BarakoCMS.Tests;

/// <summary>
/// 10,000 inputs from one seed: half random, half mutated from valid formulas. Each must parse, or
/// give a parse error, with no other exception and in under 50 ms. Set EXPRESSION_FUZZ_SEED to replay
/// a failure; the failure message prints the seed.
/// </summary>
public class ExpressionFuzzTests
{
    private const int Count = 10_000;
    private const int FixedSeed = 1420;
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

    [Fact]
    public void Ten_thousand_inputs_parse_or_give_a_parse_error_within_50_ms()
    {
        var seed = SeedToUse();
        var inputs = Inputs(seed, Count);
        inputs.Should().HaveCount(Count);

        foreach (var warm in inputs.Take(300).Concat(Corpus))
        {
            Run(warm);
        }

        var failures = new List<string>();
        var parsed = 0;
        var refused = 0;
        for (var i = 0; i < inputs.Count; i++)
        {
            var source = inputs[i];
            var clock = Stopwatch.StartNew();
            try
            {
                var result = Check(source);
                if (result.IsValid)
                {
                    parsed++;
                    ExpressionEngine.Evaluate(result.Expression!, _ => null);
                }
                else
                {
                    refused++;
                    result.Errors.Should().NotBeEmpty();
                    result.Errors.Should().OnlyContain(e => e.Position >= 0 && !string.IsNullOrEmpty(e.Rule));
                }
            }
            catch (Exception e)
            {
                failures.Add($"#{i} threw {e.GetType().Name}: {e.Message} on {Describe(source)}");
            }

            if (clock.Elapsed > Slowest)
            {
                failures.Add($"#{i} took {clock.ElapsedMilliseconds} ms on {Describe(source)}");
            }
        }

        failures.Should().BeEmpty($"seed {seed}; replay with EXPRESSION_FUZZ_SEED={seed}");
        parsed.Should().BeGreaterThan(100, $"seed {seed}: the mutated half should keep some valid inputs");
        refused.Should().BeGreaterThan(Count / 4, $"seed {seed}: the random half should mostly be refused");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Other_seeds_hold_too(int seed)
    {
        foreach (var source in Inputs(seed, 1500))
        {
            var act = () =>
            {
                var result = Check(source);
                if (result.IsValid)
                {
                    ExpressionEngine.Evaluate(result.Expression!, _ => null);
                }
            };

            act.Should().NotThrow($"seed {seed}, input {Describe(source)}");
        }
    }

    private static string Describe(string source) =>
        source.Length <= 120 ? $"\"{source.Replace("\0", "\\0")}\"" : $"a {source.Length} character input starting \"{source[..60].Replace("\0", "\\0")}\"";
}
