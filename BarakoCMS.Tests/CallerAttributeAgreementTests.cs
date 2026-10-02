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
/// The compiled read predicate and <see cref="ConditionEvaluator"/> agree on rules that read the
/// caller's member profile, for every profile a caller can have.
/// </summary>
/// <remarks>
/// <see cref="PermissionPredicateAgreementTests"/> holds this for the rest of the language. A
/// variable adds a second input, the profile, and the profiles that matter are the ones with
/// something missing: none at all, an empty one, one without the attribute, one with an empty
/// value. A list is served from the predicate and a single entry from the evaluator, so a
/// disagreement here is an entry a caller can list and not open, or open and not list.
/// </remarks>
[Collection("Sequential")]
public class CallerAttributeAgreementTests
{
    private readonly IntegrationTestFixture _fixture;

    public CallerAttributeAgreementTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly string Longest = new('n', 256);

    /// <summary>
    /// What a field can hold. The list and the object are here because a caller attribute must
    /// match neither, in memory or in SQL, whatever text the profile holds.
    /// </summary>
    private static readonly object?[] Values =
    [
        "north",
        "North",
        "south",
        "",
        null,
        42L,
        "42",
        1.5m,
        "1.5",
        true,
        "True",
        "$CURRENT_USER.branch",
        "it's",
        "50%",
        "a\\b",
        Longest,
        new List<object> { "north" },
        new Dictionary<string, object> { ["name"] = "north" },
    ];

    private static readonly string[] Scalars =
    [
        "$CURRENT_USER.branch",
        "$CURRENT_USER.ward",
        "$CURRENT_USER.level",
        "$CURRENT_USER.missing",
        "$CURRENT_USER.Branch",
        "$CURRENT_USER.",
        "$CURRENT_USER",
        "north",
    ];

    private static readonly (string Name, Dictionary<string, string>? Profile)[] Profiles =
    [
        ("no membership", null),
        ("an empty profile", new Dictionary<string, string>()),
        ("branch north", new Dictionary<string, string> { ["branch"] = "north" }),
        ("branch south, ward north, level 42",
            new Dictionary<string, string> { ["branch"] = "south", ["ward"] = "north", ["level"] = "42" }),
        ("an empty branch", new Dictionary<string, string> { ["branch"] = "", ["ward"] = "True" }),
        ("a branch that is itself a variable",
            new Dictionary<string, string> { ["branch"] = "$CURRENT_USER", ["ward"] = "$CURRENT_USER.branch" }),
        ("the JSON text of a list and of an object",
            new Dictionary<string, string> { ["branch"] = "[\"north\"]", ["ward"] = "{\"name\": \"north\"}" }),
        ("a quote and a percent sign", new Dictionary<string, string> { ["branch"] = "it's", ["ward"] = "50%" }),
        ("a backslash and a decimal", new Dictionary<string, string> { ["branch"] = "a\\b", ["ward"] = "1.5" }),
        ("the longest value a profile holds", new Dictionary<string, string> { ["branch"] = Longest }),
    ];

    private static object Stored(object? value) => value ?? JsonDocument.Parse("null").RootElement;

    private static Dictionary<string, object> Data(Random rng)
    {
        var data = new Dictionary<string, object>();

        foreach (var field in new[] { "Branch", "Ward" })
        {
            if (rng.Next(4) == 0) continue;

            data[field] = Stored(Values[rng.Next(Values.Length)]);
        }

        return data;
    }

    private static Dictionary<string, object> One(string field, string op, object expected) => new()
    {
        [field] = new Dictionary<string, object> { [op] = expected },
    };

    private static Dictionary<string, object> Conditions(Random rng)
    {
        var conditions = new Dictionary<string, object>();

        foreach (var _ in Enumerable.Range(0, rng.Next(1, 3)))
        {
            var field = new[] { "Branch", "Ward", "Missing", "$createdBy" }[rng.Next(4)];
            var op = new[] { "_eq", "_ne", "_in", "_nin" }[rng.Next(4)];

            // One in six puts a variable where a list belongs, which the evaluator denies.
            object expected = op is "_in" or "_nin" && rng.Next(6) != 0
                ? Enumerable.Range(0, rng.Next(0, 3))
                    .Select(_ => Values[rng.Next(Values.Length)] ?? "north")
                    .ToList()
                : Scalars[rng.Next(Scalars.Length)];

            conditions[field] = new Dictionary<string, object> { [op] = expected };
        }

        return conditions;
    }

    [Fact]
    public async Task The_compiled_predicate_selects_what_the_evaluator_allows_for_every_profile()
    {
        var rng = new Random(20261002);
        var evaluator = new ConditionEvaluator();
        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };
        var type = "attr" + Guid.NewGuid().ToString("N")[..8];

        // Fixed entries first, so the named rules below have something to select and something to
        // leave out whatever the generator draws.
        var seeded = new List<Dictionary<string, object>>
        {
            new() { ["Branch"] = "north", ["Ward"] = "north" },
            new() { ["Branch"] = "north" },
            new() { ["Branch"] = "south", ["Ward"] = "True" },
            new() { ["Branch"] = 42L },
            new() { ["Branch"] = Stored(null) },
            new() { ["Branch"] = "" },
            new() { ["Ward"] = "north" },
            new() { ["Branch"] = "$CURRENT_USER" },
            new() { ["Branch"] = new List<object> { "north" }, ["Ward"] = new Dictionary<string, object> { ["name"] = "north" } },
            new() { ["Branch"] = new Dictionary<string, object> { ["name"] = "north" } },
            new() { ["Branch"] = "it's", ["Ward"] = "50%" },
            new() { ["Branch"] = "a\\b", ["Ward"] = 1.5m },
            new() { ["Branch"] = Longest },
        };
        seeded.AddRange(Enumerable.Range(0, 47).Select(_ => Data(rng)));

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            foreach (var data in seeded)
            {
                session.Store(new Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = type,
                    Data = data,
                    CreatedBy = rng.Next(2) == 0 ? Caller : Guid.NewGuid(),
                    Status = ContentStatus.Draft,
                });
            }

            await session.SaveChangesAsync();
        }

        // Read back, for the reason PermissionPredicateAgreementTests gives: the evaluator has to
        // see the JsonElement values production sees, not the CLR ones this test wrote.
        List<Content> contents;
        using (var scope = _fixture.Services.CreateScope())
        {
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            contents = (await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync()).ToList();
        }

        contents.Should().HaveCount(60, "every seeded entry has to come back");

        // The text the evaluator would compare for a list or an object: after a read it is the
        // name of the CLR type, which no generator would think to put in a profile. Taken from the
        // stored entries so it is whatever production really sees.
        var listFields = contents
            .Where(c => c.Data.TryGetValue("Branch", out var v) && v is System.Collections.IList)
            .ToList();
        var bagFields = contents
            .Where(c => c.Data.TryGetValue("Branch", out var v) && v is System.Collections.IDictionary)
            .ToList();
        listFields.Should().NotBeEmpty("an entry whose Branch is a list was seeded");
        bagFields.Should().NotBeEmpty("an entry whose Branch is an object was seeded");

        var profiles = Profiles.ToList();
        profiles.Add(("the text a list and an object read as in memory", new Dictionary<string, string>
        {
            ["branch"] = listFields[0].Data["Branch"].ToString()!,
            ["ward"] = bagFields[0].Data["Branch"].ToString()!,
        }));
        var nonScalar = listFields.Concat(bagFields).Select(c => c.Id).ToHashSet();

        var rules = new List<Dictionary<string, object>>
        {
            One("Branch", "_eq", "$CURRENT_USER.branch"),
            One("Branch", "_ne", "$CURRENT_USER.branch"),
            One("Ward", "_eq", "$CURRENT_USER.ward"),
            One("Branch", "_eq", "$CURRENT_USER.level"),
        };
        rules.AddRange(Enumerable.Range(0, 80).Select(_ => Conditions(rng)));

        var compiled = 0;
        var selectedSome = 0;

        foreach (var conditions in rules)
        {
            foreach (var (name, profile) in profiles)
            {
                var rule = new PermissionRule { Enabled = true, Conditions = conditions };
                var predicate = PermissionPredicateCompiler.Compile([rule], user.Id, profile);

                if (!predicate.Compiled) continue;

                compiled++;

                var expected = contents
                    .Where(c => evaluator.Evaluate(conditions, c, user, profile))
                    .Select(c => c.Id)
                    .OrderBy(id => id)
                    .ToList();

                using var scope = _fixture.Services.CreateScope();
                var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();

                var actual = (await query.Query<Content>()
                        .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                        .ToListAsync())
                    .Select(c => c.Id)
                    .OrderBy(id => id)
                    .ToList();

                actual.Should().Equal(expected,
                    "the predicate and the evaluator must select the same rows for {0} with {1}. "
                  + "Predicate: {2} with [{3}]",
                    JsonSerializer.Serialize(conditions),
                    name,
                    predicate.Sql,
                    string.Join(", ", predicate.Parameters));

                if (expected.Count > 0) selectedSome++;
            }
        }

        compiled.Should().BeGreaterThan(200,
            "a compiler that declines everything makes this vacuous, and it compiled {0} of {1}",
            compiled, rules.Count * profiles.Count);
        selectedSome.Should().BeGreaterThan(10, "agreeing on nothing at all is not agreement");

        // Agreement alone would be satisfied by both sides granting on a list. Under the two named
        // rules, for every profile, neither side selects an entry whose Branch is a list or an object.
        foreach (var conditions in rules.Take(2))
        {
            foreach (var (name, profile) in profiles)
            {
                contents
                    .Where(c => nonScalar.Contains(c.Id) && evaluator.Evaluate(conditions, c, user, profile))
                    .Should().BeEmpty("a list or an object matches no caller attribute, with {0}", name);
            }
        }

        // And the values with a quote, a percent sign, a backslash and 256 characters each select
        // the entry that holds exactly that text, so they reach the comparison whole.
        foreach (var text in new[] { "it's", "a\\b", Longest })
        {
            var conditions = One("Branch", "_eq", "$CURRENT_USER.branch");
            var profile = new Dictionary<string, string> { ["branch"] = text };
            var predicate = PermissionPredicateCompiler.Compile(
                [new PermissionRule { Enabled = true, Conditions = conditions }], user.Id, profile);

            using var scope = _fixture.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var bySql = await query.Query<Content>()
                .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                .ToListAsync();

            bySql.Should().NotBeEmpty("an entry holding exactly that text was seeded");
            bySql.Should().OnlyContain(c => (c.Data["Branch"] as string) == text);
            contents.Where(c => evaluator.Evaluate(conditions, c, user, profile)).Select(c => c.Id)
                .Should().BeEquivalentTo(bySql.Select(c => c.Id));
        }
    }

    [Fact]
    public async Task The_named_rule_selects_the_callers_branch_through_both()
    {
        var evaluator = new ConditionEvaluator();
        var user = new User { Id = Caller };
        var type = "attr" + Guid.NewGuid().ToString("N")[..8];
        var conditions = One("Branch", "_eq", "$CURRENT_USER.branch");
        var rule = new PermissionRule { Enabled = true, Conditions = conditions };

        var north = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var south = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            foreach (var id in north)
                session.Store(new Content { Id = id, ContentType = type, Data = new() { ["Branch"] = "north" } });
            foreach (var id in south)
                session.Store(new Content { Id = id, ContentType = type, Data = new() { ["Branch"] = "south" } });
            session.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Data = new() { ["Title"] = "no branch" } });

            await session.SaveChangesAsync();
        }

        using var read = _fixture.Services.CreateScope();
        var query = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var contents = await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync();
        contents.Should().HaveCount(6);

        async Task<List<Guid>> BySqlAsync(Dictionary<string, string>? profile)
        {
            var predicate = PermissionPredicateCompiler.Compile([rule], user.Id, profile);
            predicate.Compiled.Should().BeTrue("this is the rule the issue names, and a list has to be able to page it");

            return (await query.Query<Content>()
                    .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                    .ToListAsync())
                .Select(c => c.Id)
                .ToList();
        }

        List<Guid> ByEvaluator(Dictionary<string, string>? profile) =>
            contents.Where(c => evaluator.Evaluate(conditions, c, user, profile)).Select(c => c.Id).ToList();

        var northProfile = new Dictionary<string, string> { ["branch"] = "north" };
        var southProfile = new Dictionary<string, string> { ["branch"] = "south" };
        var wardOnly = new Dictionary<string, string> { ["ward"] = "north" };

        (await BySqlAsync(northProfile)).Should().HaveCount(2).And.BeEquivalentTo(north);
        ByEvaluator(northProfile).Should().HaveCount(2).And.BeEquivalentTo(north);

        (await BySqlAsync(southProfile)).Should().HaveCount(3).And.BeEquivalentTo(south);
        ByEvaluator(southProfile).Should().HaveCount(3).And.BeEquivalentTo(south);

        (await BySqlAsync(null)).Should().BeEmpty("a caller with no membership has no branch");
        ByEvaluator(null).Should().BeEmpty();

        (await BySqlAsync(wardOnly)).Should().BeEmpty("a caller with no value for branch matches nothing");
        ByEvaluator(wardOnly).Should().BeEmpty();
    }
}
