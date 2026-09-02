using System.Text.Json;
using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The compiled read predicate and <see cref="ConditionEvaluator"/> have to agree, on generated
/// input rather than on examples.
/// </summary>
/// <remarks>
/// Two evaluators for one condition language drift. A new operator gets added to the in-memory one
/// and not the SQL one, or a null is handled differently on one side, and the difference shows up as
/// rows silently appearing or vanishing rather than as an error. Nothing else in this suite can tell
/// you that has happened.
///
/// <b>If this test is deleted, PermissionPredicateCompiler becomes a liability.</b> It is the only
/// thing standing between "a faster list" and "a list that shows somebody a row they may not read".
///
/// The generation is deliberately unkind: fields that are missing from some documents, values that
/// are null, numbers and booleans where a string is expected, empty lists, and values that differ
/// only by case. Those are where two implementations of the same rule diverge, and none of them
/// would appear in a handful of hand-written examples.
/// </remarks>
[Collection("Sequential")]
public class PermissionPredicateAgreementTests
{
    private readonly IntegrationTestFixture _fixture;

    public PermissionPredicateAgreementTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>Fixed seed, so a failure is reproducible and a rerun is not a new experiment.</summary>
    private static Random Rng() => new(20260902);

    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("99999999-8888-7777-6666-555555555555");

    /// <summary>
    /// Values chosen to sit on the edges the two implementations could disagree about.
    /// </summary>
    /// <remarks>
    /// "True" and "true" are both here, and both are load bearing. A boolean in the data compares as
    /// <c>bool.TrueString</c> through the evaluator and as lower case through Postgres, and a
    /// generator that never produced either spelling as an EXPECTED value would let that divergence
    /// through: rendering booleans lower case was mutated in and this test stayed green until these
    /// were added.
    /// </remarks>
    private static readonly object?[] Values =
    [
        "alpha",
        "Alpha",          // differs only by case, and Data lookups are case sensitive
        "beta",
        "",               // empty string is not the same as absent
        null,             // JSON null, whose ToString is the text "null" and whose #>> is SQL NULL
        42L,              // a number compared against a string
        "42",             // and the text of one, which has to match it
        true,             // a boolean, whose ToString is "True" and whose SQL rendering is "true"
        "True",           // the text .NET gives for it, capitalised
        "true",           // the text Postgres gives for it, which must NOT match
        "$CURRENT_USER",  // the literal, which is only special on the condition side
    ];

    private static readonly string[] Fields = ["Title", "Owner", "title", "Missing"];

    private static Dictionary<string, object> Data(Random rng)
    {
        var data = new Dictionary<string, object>();

        foreach (var field in new[] { "Title", "Owner", "title" })
        {
            // A third of documents leave the field out, which is the case that denies in the
            // evaluator whatever the operator says.
            if (rng.Next(3) == 0) continue;

            var value = Values[rng.Next(Values.Length)];
            if (value is not null) data[field] = value;
            else data[field] = JsonDocument.Parse("null").RootElement;
        }

        return data;
    }

    private static Dictionary<string, object> Conditions(Random rng)
    {
        var conditions = new Dictionary<string, object>();

        foreach (var _ in Enumerable.Range(0, rng.Next(1, 3)))
        {
            var field = rng.Next(6) == 0
                ? (rng.Next(2) == 0 ? "$createdBy" : "$lastModifiedBy")
                : Fields[rng.Next(Fields.Length)];

            var op = new[] { "_eq", "_ne", "_in", "_nin" }[rng.Next(4)];

            object expected = op is "_in" or "_nin"
                ? Enumerable.Range(0, rng.Next(0, 3))
                    .Select(_ => Values[rng.Next(Values.Length)] ?? "alpha")
                    .ToList()
                : Values[rng.Next(Values.Length)] ?? "alpha";

            conditions[field] = new Dictionary<string, object> { [op] = expected };
        }

        return conditions;
    }

    [Fact]
    public async Task The_compiled_predicate_selects_exactly_what_the_evaluator_allows()
    {
        var rng = Rng();
        var evaluator = new ConditionEvaluator();
        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };

        var type = "agree" + Guid.NewGuid().ToString("N")[..8];

        // The documents, written once and reused by every rule below.
        var contents = new List<Content>();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            foreach (var i in Enumerable.Range(0, 60))
            {
                var content = new Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = type,
                    Data = Data(rng),
                    CreatedBy = rng.Next(2) == 0 ? Caller : Other,
                    LastModifiedBy = rng.Next(2) == 0 ? Caller : Other,
                    Status = ContentStatus.Draft,
                };

                contents.Add(content);
                session.Store(content);
            }

            await session.SaveChangesAsync();
        }

        contents.Should().HaveCount(60, "the comparisons below prove nothing over an empty store");

        // Read back rather than reused. This is not tidiness: a Content held in memory has its Data
        // values as the CLR types they were written with, while one loaded from Marten has them as
        // JsonElement, and ConditionEvaluator compares ToString() of whatever it is handed. A bool
        // is "True" one way and "true" the other. Comparing the predicate against the in-memory copy
        // would test a shape production never sees, and would have hidden exactly that.
        using (var scope = _fixture.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stored = await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync();

            stored.Should().HaveCount(contents.Count, "every seeded document has to come back");

            contents.Clear();
            contents.AddRange(stored);
        }

        var compiled = 0;
        var checkedRules = 0;

        foreach (var _ in Enumerable.Range(0, 120))
        {
            var conditions = Conditions(rng);

            var rule = new PermissionRule { Enabled = true, Conditions = conditions };
            var predicate = PermissionPredicateCompiler.Compile([rule], user.Id);

            if (!predicate.Compiled) continue;

            compiled++;

            // What the evaluator says, in memory, over the same documents.
            var expected = contents
                .Where(c => evaluator.Evaluate(conditions, c, user))
                .Select(c => c.Id)
                .OrderBy(id => id)
                .ToList();

            // What the database says.
            using var scope = _fixture.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();

            var actual = (await query.Query<Content>()
                    .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                    .ToListAsync())
                .Select(c => c.Id)
                .OrderBy(id => id)
                .ToList();

            if (!actual.SequenceEqual(expected))
            {
                var onlySql = actual.Except(expected).ToList();
                var onlyEvaluator = expected.Except(actual).ToList();

                var offenders = contents
                    .Where(c => onlySql.Contains(c.Id) || onlyEvaluator.Contains(c.Id))
                    .Take(3)
                    .Select(c => (onlySql.Contains(c.Id) ? "SQL only " : "EVAL only ")
                               + JsonSerializer.Serialize(c.Data));

                actual.Should().Equal(expected,
                    "the predicate and the evaluator must select the same rows, and they disagreed on "
                  + "{0}. Predicate: {1} with [{2}]. Offending documents: {3}",
                    JsonSerializer.Serialize(conditions),
                    predicate.Sql,
                    string.Join(", ", predicate.Parameters),
                    string.Join(" | ", offenders));
            }

            checkedRules++;
        }

        // The gate that stops this passing by compiling nothing. A compiler that returned None for
        // every rule would satisfy every assertion above by never running one.
        compiled.Should().BeGreaterThan(40,
            "a compiler that declines everything makes this test vacuous, and it declined {0} of 120",
            120 - compiled);

        checkedRules.Should().Be(compiled);
    }

    [Fact]
    public void The_shapes_that_cannot_be_compiled_are_declined_rather_than_guessed()
    {
        // Each of these denies or compares in a way the SQL cannot reproduce faithfully, so the
        // compiler has to return None and let the caller evaluate per item. Asserted one by one
        // because "it declined" is only correct if it declined for these and not for everything,
        // which the test above holds from the other side.
        var cases = new (string Name, Dictionary<string, object> Conditions)[]
        {
            ("$status, whose enum is stored as a number",
                new() { ["$status"] = new Dictionary<string, object> { ["_eq"] = "Published" } }),
            ("an unknown document property",
                new() { ["$invented"] = new Dictionary<string, object> { ["_eq"] = "x" } }),
            ("an unknown operator",
                new() { ["Title"] = new Dictionary<string, object> { ["_gt"] = "x" } }),
            ("a number on the expected side",
                new() { ["Title"] = new Dictionary<string, object> { ["_eq"] = 42L } }),
            ("a boolean on the expected side",
                new() { ["Title"] = new Dictionary<string, object> { ["_eq"] = true } }),
            ("_in with a scalar rather than a list",
                new() { ["Title"] = new Dictionary<string, object> { ["_in"] = "x" } }),
        };

        foreach (var (name, conditions) in cases)
        {
            var rule = new PermissionRule { Enabled = true, Conditions = conditions };

            PermissionPredicateCompiler.Compile([rule], Caller).Compiled
                .Should().BeFalse("{0} cannot be compiled faithfully, so it has to fall back", name);
        }
    }

    [Fact]
    public void A_rule_with_no_conditions_selects_everything_and_no_rules_selects_nothing()
    {
        // The two constants, and they are opposite ends. Getting them the wrong way round is the
        // single worst defect this file could hide, so they are pinned rather than inferred.
        var unconditional = new PermissionRule { Enabled = true, Conditions = new() };

        PermissionPredicateCompiler.Compile([unconditional], Caller).Sql.Should().Be("TRUE");
        PermissionPredicateCompiler.Compile([], Caller).Sql.Should().Be("FALSE");

        var disabled = new PermissionRule { Enabled = false, Conditions = new() };
        PermissionPredicateCompiler.Compile([disabled], Caller).Sql.Should().Be("FALSE",
            "a disabled rule grants nothing, and a set of only disabled rules grants nothing");
    }
}
