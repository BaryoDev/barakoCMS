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
[Collection("Integration1")]
public class PermissionPredicateAgreementTests
{
    private readonly IntegrationTestFixture _fixture;

    public PermissionPredicateAgreementTests(IntegrationTestFixture fixture) => _fixture = fixture;

    /// <summary>Fixed seed, so a failure is reproducible and a rerun is not a new experiment.</summary>
    private static Random Rng() => new(20260902);

    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Other = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly Guid B1 = Guid.Parse("b1b1b1b1-0000-0000-0000-000000000001");
    private static readonly Guid B2 = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    /// <summary>The request's branch, as PermissionResolver puts it in the profile.</summary>
    private static readonly Dictionary<string, string> Profile = new() { ["branch"] = B1.ToString() };

    /// <summary>A row's branch: one of two, or none, which is where the two sides could part.</summary>
    private static Guid? BranchOf(Random rng) => rng.Next(3) switch { 0 => B1, 1 => B2, _ => null };

    /// <summary>What a <c>$branch</c> condition compares with, the variable included.</summary>
    private static readonly object[] BranchValues = [B1.ToString(), B2.ToString(), "$CURRENT_BRANCH"];

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
            var pick = rng.Next(6);
            var field = pick switch
            {
                0 => rng.Next(2) == 0 ? "$createdBy" : "$lastModifiedBy",
                1 => "$branch",
                _ => Fields[rng.Next(Fields.Length)],
            };

            var values = field == "$branch" ? BranchValues : Values;

            var op = new[] { "_eq", "_ne", "_in", "_nin" }[rng.Next(4)];

            object expected = op is "_in" or "_nin"
                ? Enumerable.Range(0, rng.Next(0, 3))
                    .Select(_ => values[rng.Next(values.Length)] ?? "alpha")
                    .ToList()
                : values[rng.Next(values.Length)] ?? "alpha";

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
                    BranchId = BranchOf(rng),
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
            var predicate = PermissionPredicateCompiler.Compile([rule], user.Id, Profile);

            if (!predicate.Compiled) continue;

            compiled++;

            // What the evaluator says, in memory, over the same documents.
            var expected = contents
                .Where(c => evaluator.Evaluate(conditions, c, user, Profile))
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

    /// <summary>Three rows of a fresh type, in B1, in B2 and in no branch, read back from storage.</summary>
    private async Task<(string Type, Content InB1, Content InB2, Content NoBranch, List<Content> Stored)> BranchRowsAsync()
    {
        var type = "agreebr" + Guid.NewGuid().ToString("N")[..8];
        var rows = new[] { B1, B2, (Guid?)null }
            .Select(b => new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Data = new() { ["Title"] = "x" },
                BranchId = b, Status = ContentStatus.Draft,
            })
            .ToArray();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(rows);
            await session.SaveChangesAsync();
        }

        using var read = _fixture.Services.CreateScope();
        var stored = await read.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync();
        return (type, rows[0], rows[1], rows[2], stored.ToList());
    }

    /// <summary>The ids each side selects for one rule and one profile.</summary>
    private async Task<(List<Guid> Evaluator, List<Guid> Sql)> BothAsync(
        string type, List<Content> stored, Dictionary<string, object> conditions, IReadOnlyDictionary<string, string>? profile)
    {
        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };
        var evaluated = stored.Where(c => new ConditionEvaluator().Evaluate(conditions, c, user, profile))
            .Select(c => c.Id).OrderBy(id => id).ToList();

        var predicate = PermissionPredicateCompiler.Compile(
            [new PermissionRule { Enabled = true, Conditions = conditions }], Caller, profile);
        predicate.Compiled.Should().BeTrue("$branch and $CURRENT_BRANCH compile");

        using var scope = _fixture.Services.CreateScope();
        var sql = (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Content>()
                .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                .ToListAsync())
            .Select(c => c.Id).OrderBy(id => id).ToList();

        return (evaluated, sql);
    }

    private static Dictionary<string, object> OnBranch(string op) =>
        new() { ["$branch"] = new Dictionary<string, object> { [op] = "$CURRENT_BRANCH" } };

    [Fact]
    public async Task Branch_rules_pick_the_same_rows_when_a_row_has_no_branch()
    {
        var (type, _, inB2, noBranch, stored) = await BranchRowsAsync();

        var (evaluated, sql) = await BothAsync(type, stored, OnBranch("_ne"), Profile);

        var expected = new[] { inB2.Id, noBranch.Id }.OrderBy(id => id).ToList();
        evaluated.Should().Equal(expected);
        sql.Should().Equal(expected);
    }

    [Fact]
    public async Task A_caller_with_no_branch_variable_matches_nothing_on_CURRENT_BRANCH()
    {
        var (type, _, _, _, stored) = await BranchRowsAsync();

        foreach (var op in new[] { "_eq", "_ne" })
        {
            var (evaluated, sql) = await BothAsync(type, stored, OnBranch(op), new Dictionary<string, string>());
            evaluated.Should().BeEmpty("{0} against a branch the caller does not have denies", op);
            sql.Should().BeEmpty("{0} against a branch the caller does not have denies", op);
        }
    }

    private static readonly object[] StatusExpected =
        ["Published", "Draft", "Archived", "Scheduled", "published", "PUBLISHED", "1", "", "Unknown", "$CURRENT_USER"];

    [Fact]
    public async Task A_status_condition_is_compiled_and_selects_what_the_evaluator_allows()
    {
        var evaluator = new ConditionEvaluator();
        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };
        var type = "status" + Guid.NewGuid().ToString("N")[..8];

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var status in Enum.GetValues<ContentStatus>())
            {
                foreach (var _ in Enumerable.Range(0, 2))
                {
                    session.Store(new Content
                    {
                        Id = Guid.NewGuid(), ContentType = type, Status = status,
                        CreatedBy = Caller, LastModifiedBy = Caller,
                    });
                }
            }
            await session.SaveChangesAsync();
        }

        using var read = _fixture.Services.CreateScope();
        var query = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync();
        stored.Should().HaveCount(Enum.GetValues<ContentStatus>().Length * 2);

        var rules = new List<Dictionary<string, object>>();
        foreach (var expected in StatusExpected)
        {
            foreach (var op in new[] { "_eq", "_ne" })
                rules.Add(new() { ["$status"] = new Dictionary<string, object> { [op] = expected } });
        }
        foreach (var op in new[] { "_in", "_nin" })
        {
            rules.Add(new() { ["$status"] = new Dictionary<string, object> { [op] = new List<object>() } });
            rules.Add(new() { ["$status"] = new Dictionary<string, object> { [op] = new List<object> { "Published", "Scheduled" } } });
            rules.Add(new() { ["$Status"] = new Dictionary<string, object> { [op] = new List<object> { "published", "Draft" } } });
        }
        rules.Add(new()
        {
            ["$status"] = new Dictionary<string, object> { ["_eq"] = "Published" },
            ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
        });

        foreach (var conditions in rules)
        {
            var predicate = PermissionPredicateCompiler.Compile([new PermissionRule { Enabled = true, Conditions = conditions }], user.Id);
            predicate.Compiled.Should().BeTrue("{0} must compile", JsonSerializer.Serialize(conditions));

            var byEvaluator = stored.Where(c => evaluator.Evaluate(conditions, c, user)).Select(c => c.Id).Order().ToList();
            var bySql = (await query.Query<Content>()
                    .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                    .ToListAsync())
                .Select(c => c.Id).Order().ToList();

            bySql.Should().Equal(byEvaluator, "the predicate and the evaluator disagreed on {0}", JsonSerializer.Serialize(conditions));
        }

        var published = new Dictionary<string, object> { ["$status"] = new Dictionary<string, object> { ["_eq"] = "Published" } };
        stored.Count(c => evaluator.Evaluate(published, c, user)).Should().Be(2, "only the Published rows match");
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
            ("$status compared with a number",
                new() { ["$status"] = new Dictionary<string, object> { ["_eq"] = 1L } }),
            ("$status with a list operator given a scalar",
                new() { ["$status"] = new Dictionary<string, object> { ["_in"] = "Published" } }),
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
