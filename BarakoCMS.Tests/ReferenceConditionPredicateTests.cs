using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A rule that follows a reference becomes a read predicate, and the predicate selects exactly the
/// rows the per-entry check allows.
/// </summary>
/// <remarks>
/// The entries list pages and counts in the database only when <see cref="PermissionResolver"/>
/// hands it a predicate. Without one it still answers correctly, by loading the type and checking
/// each entry, so a test through the list cannot tell the two apart. These ask the resolver.
///
/// <see cref="PermissionPredicateAgreementTests"/> holds the same property for a rule that reads
/// the row itself.
/// </remarks>
[Collection("Sequential")]
public class ReferenceConditionPredicateTests
{
    private const string Follows = "Class.InstructorUser";

    private readonly IntegrationTestFixture _fixture;

    public ReferenceConditionPredicateTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static PermissionRule Where(string key, string op, object expected) => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object> { [key] = new Dictionary<string, object> { [op] = expected } },
    };

    private async Task<(string Classes, string Enrollments)> TypesAsync()
    {
        var suffix = Guid.NewGuid().ToString("n")[..10];
        var classes = $"prdclass{suffix}";
        var enrollments = $"prdenrol{suffix}";

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = classes, DisplayName = classes,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "InstructorUser", DisplayName = "Instructor", Type = "string" },
                new FieldDefinition { Name = "Seats", DisplayName = "Seats", Type = "int" },
            ],
        });

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = enrollments, DisplayName = enrollments,
            Fields =
            [
                new FieldDefinition { Name = "Student", DisplayName = "Student", Type = "string" },
                new FieldDefinition { Name = "Class", DisplayName = "Class", Type = "reference", ReferenceType = classes },
            ],
        });

        await session.SaveChangesAsync();
        return (classes, enrollments);
    }

    private Task<User> CallerAsync(params ContentTypePermission[] permissions) => CallerAsync(_ => permissions);

    /// <summary>A stored user with one role. The permissions are built from the user's id.</summary>
    private async Task<User> CallerAsync(Func<Guid, ContentTypePermission[]> permissions)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Predicate_{Guid.NewGuid():n}",
            Permissions = [.. permissions(userId)],
        };
        session.Store(role);

        var user = new User
        {
            Id = userId,
            Username = $"predicate_{Guid.NewGuid():n}",
            Email = $"predicate_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        return user;
    }

    private async Task<List<Guid>> StoreAsync(
        string type,
        IEnumerable<Dictionary<string, object>> entries,
        SensitivityLevel sensitivity = SensitivityLevel.Public,
        ContentStatus status = ContentStatus.Published)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var ids = new List<Guid>();
        foreach (var data in entries)
        {
            var content = new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                Sensitivity = sensitivity,
                Data = data,
            };
            session.Store(content);
            ids.Add(content.Id);
        }

        await session.SaveChangesAsync();
        return ids;
    }

    private static Dictionary<string, object> Class(Guid instructor, string title = "a class") => new()
    {
        ["Title"] = title,
        ["InstructorUser"] = instructor.ToString(),
        ["Seats"] = 30L,
    };

    private static Dictionary<string, object> Enrollment(object? @class)
    {
        var data = new Dictionary<string, object> { ["Student"] = "a student" };
        if (@class is not null) data["Class"] = @class;
        return data;
    }

    /// <summary>
    /// The three ways one question is answered: what the predicate selects of a type, what a pass
    /// over every entry of it allows, and what a check on each entry alone allows.
    /// </summary>
    /// <remarks>
    /// <c>Allowed</c> is the last of them, each entry asked of a resolver of its own, the way a get
    /// by id asks: the referenced entry is loaded and nothing is resolved to a set. The pass is one
    /// resolver over all of them, the way a list the database cannot page asks, and it is held equal
    /// to <c>Allowed</c> here, for every caller of this method.
    /// </remarks>
    private async Task<(ReadPredicate Predicate, List<Guid> Selected, List<Guid> Allowed, int Stored)> AskAsync(
        Guid userId, string type)
    {
        ReadPredicate predicate;
        List<Guid> selected = [];
        IReadOnlyList<Content> stored;

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();
            var user = await session.LoadAsync<User>(userId);
            user.Should().NotBeNull();

            predicate = await resolver.ReadPredicateAsync(user!, type);

            if (predicate.Compiled)
            {
                selected = (await session.Query<Content>()
                        .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                        .ToListAsync())
                    .Select(c => c.Id)
                    .ToList();
            }

            // Read back, so the per-entry checks see the shapes a request sees.
            stored = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync();
        }

        var passed = new List<Guid>();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();
            var user = await session.LoadAsync<User>(userId);

            foreach (var entry in stored)
            {
                if (await resolver.CanPerformActionAsync(user!, type, "read", entry))
                    passed.Add(entry.Id);
            }
        }

        var allowed = new List<Guid>();
        foreach (var entry in stored)
        {
            using var scope = _fixture.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();
            var user = await session.LoadAsync<User>(userId);

            if (await resolver.CanPerformActionAsync(user!, type, "read", entry))
                allowed.Add(entry.Id);
        }

        passed.Should().HaveCount(allowed.Count, "a pass over every entry and a check on each alone are one question");
        passed.Should().BeEquivalentTo(allowed);

        return (predicate, selected, allowed, stored.Count);
    }

    [Fact]
    public async Task A_rule_that_follows_a_reference_compiles_and_selects_what_the_per_entry_check_allows()
    {
        var (classes, enrollments) = await TypesAsync();
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = Where("InstructorUser", "_eq", "$CURRENT_USER") },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(Follows, "_eq", "$CURRENT_USER") });

        var mine = await StoreAsync(classes, [Class(caller.Id), Class(caller.Id)]);
        var theirs = await StoreAsync(classes, [Class(Guid.NewGuid())]);
        var guarded = await StoreAsync(classes, [Class(caller.Id)], SensitivityLevel.Sensitive);

        var expected = await StoreAsync(enrollments,
        [
            Enrollment(mine[0].ToString()),
            Enrollment(mine[0].ToString()),
            Enrollment(mine[1].ToString()),
            Enrollment(mine[1].ToString().ToUpperInvariant()),
        ]);

        await StoreAsync(enrollments,
        [
            Enrollment(theirs[0].ToString()),
            Enrollment(theirs[0].ToString()),
            Enrollment(guarded[0].ToString()),
            Enrollment(mine[0].ToString("N")),
            Enrollment(Guid.NewGuid().ToString()),
            Enrollment("not an id"),
            Enrollment(42L),
            Enrollment(null),
            new()
            {
                ["Student"] = "forger",
                ["Class"] = theirs[0].ToString(),
                [Follows] = caller.Id.ToString(),
            },
        ]);

        var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

        stored.Should().Be(13);
        predicate.Compiled.Should().BeTrue("the list pages in the database only when it is handed a predicate");
        predicate.Sql!.Count(ch => ch == '?').Should().Be(predicate.Parameters.Length);

        allowed.Should().HaveCount(4);
        allowed.Should().BeEquivalentTo(expected);

        selected.Should().HaveCount(4);
        selected.Should().BeEquivalentTo(allowed);
    }

    [Fact]
    public async Task Every_operator_selects_what_the_per_entry_check_allows()
    {
        // Each rule is handed the caller's id, for the ones that write it into a list.
        var rules = new (string Name, Func<Guid, PermissionRule> Rule, int Expected)[]
        {
            ("equal to the caller", _ => Where(Follows, "_eq", "$CURRENT_USER"), 2),
            ("not equal to the caller", _ => Where(Follows, "_ne", "$CURRENT_USER"), 1),
            ("in a list holding the caller", id => Where(Follows, "_in", new List<object> { id.ToString(), "nobody" }), 2),
            ("not in a list holding the caller", id => Where(Follows, "_nin", new List<object> { id.ToString() }), 1),
            ("a title, with a condition on the row beside it", _ => new PermissionRule
            {
                Enabled = true,
                Conditions = new Dictionary<string, object>
                {
                    ["Class.Title"] = new Dictionary<string, object> { ["_eq"] = "algebra" },
                    ["Student"] = new Dictionary<string, object> { ["_eq"] = "a student" },
                },
            }, 2),
        };

        foreach (var (name, rule, expected) in rules)
        {
            // Types of its own for each rule, so one rule's entries do not count towards another's.
            var (classes, enrollments) = await TypesAsync();

            var caller = await CallerAsync(id =>
            [
                new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = enrollments, Read = rule(id) },
            ]);

            var taught = await StoreAsync(classes, [Class(caller.Id, "algebra")]);
            var other = await StoreAsync(classes, [Class(Guid.NewGuid(), "biology")]);
            var unstaffed = await StoreAsync(classes, [new() { ["Title"] = "nobody yet" }]);

            var seeded = await StoreAsync(enrollments,
            [
                Enrollment(taught[0].ToString()),
                Enrollment(taught[0].ToString()),
                Enrollment(other[0].ToString()),
                Enrollment(unstaffed[0].ToString()),
                Enrollment(null),
            ]);

            var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

            stored.Should().Be(5);
            predicate.Compiled.Should().BeTrue("the rule is {0}", name);

            allowed.Should().HaveCount(expected, "the rule is {0}", name);
            selected.Should().HaveCount(expected, "the rule is {0}", name);
            selected.Should().BeEquivalentTo(allowed, "the rule is {0}", name);

            // A class without the field and a row without a class are selected by nothing. The
            // negative operators are the ones that would.
            allowed.Should().NotContain(seeded[3], "the rule is {0}", name);
            allowed.Should().NotContain(seeded[4], "the rule is {0}", name);
        }
    }

    [Fact]
    public async Task An_unconditional_rule_beside_one_that_follows_a_reference_is_every_row()
    {
        var (_, enrollments) = await TypesAsync();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roles = new[]
        {
            new Role
            {
                Id = Guid.NewGuid(), Name = $"Follows_{Guid.NewGuid():n}",
                Permissions = [new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(Follows, "_eq", "$CURRENT_USER") }],
            },
            new Role
            {
                Id = Guid.NewGuid(), Name = $"Reads_{Guid.NewGuid():n}",
                Permissions = [new ContentTypePermission { ContentTypeSlug = enrollments, Read = new PermissionRule { Enabled = true } }],
            },
        };
        foreach (var role in roles) session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"both_{Guid.NewGuid():n}",
            Email = $"both_{Guid.NewGuid():n}@example.com",
            RoleIds = [.. roles.Select(r => r.Id)],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var seeded = await StoreAsync(enrollments, [Enrollment(Guid.NewGuid().ToString()), Enrollment(null)]);

        var (predicate, selected, allowed, _) = await AskAsync(user.Id, enrollments);

        predicate.Sql.Should().Be("TRUE");
        selected.Should().HaveCount(2);
        selected.Should().BeEquivalentTo(seeded);
        allowed.Should().BeEquivalentTo(seeded);
    }

    [Fact]
    public async Task A_comparison_that_is_not_text_denies_on_every_path()
    {
        var (classes, enrollments) = await TypesAsync();

        // A number is an expected value the compiler declines and the evaluator would compare as
        // text. Through a reference that would be a row a caller can open and not list, so it
        // denies, as does a list with nothing in it. Every class here holds 30 seats.
        foreach (var rule in new[]
                 {
                     Where("Class.Seats", "_eq", 30L),
                     Where("Class.Seats", "_ne", 31L),
                     Where("Class.Seats", "_in", new List<object> { 30L }),
                     Where("Class.Title", "_nin", new List<object>()),
                 })
        {
            var caller = await CallerAsync(
                new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = enrollments, Read = rule });

            var full = await StoreAsync(classes, [Class(Guid.NewGuid())]);
            var seeded = await StoreAsync(enrollments, [Enrollment(full[0].ToString()), Enrollment(full[0].ToString())]);
            seeded.Should().HaveCount(2);

            var (predicate, selected, allowed, _) = await AskAsync(caller.Id, enrollments);

            predicate.Compiled.Should().BeTrue("the rule denies every row, and that needs no per-entry pass");
            selected.Should().BeEmpty();
            allowed.Should().BeEmpty();
        }

        // The control: the same classes and enrollments under a comparison on text. Without it a
        // resolver that denied every condition following a reference would pass the loop above.
        var onText = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where("Class.Title", "_eq", "a class") });

        var (textPredicate, textSelected, textAllowed, stored) = await AskAsync(onText.Id, enrollments);

        stored.Should().Be(8, "two enrollments for each of the four rules above");
        textPredicate.Compiled.Should().BeTrue();
        textSelected.Should().HaveCount(8);
        textAllowed.Should().HaveCount(8);
    }

    [Fact]
    public async Task An_entry_of_another_type_holding_the_same_field_is_not_a_referenced_entry()
    {
        var (classes, enrollments) = await TypesAsync();
        var (lookalikes, _) = await TypesAsync();

        // Read rules on both types, so nothing but the type of the entry tells the two apart.
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = Where("InstructorUser", "_eq", "$CURRENT_USER") },
            new ContentTypePermission { ContentTypeSlug = lookalikes, Read = Where("InstructorUser", "_eq", "$CURRENT_USER") },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(Follows, "_eq", "$CURRENT_USER") });

        var real = await StoreAsync(classes, [Class(caller.Id)]);
        var lookalike = await StoreAsync(lookalikes, [Class(caller.Id)]);

        var expected = await StoreAsync(enrollments, [Enrollment(real[0].ToString())]);
        await StoreAsync(enrollments, [Enrollment(lookalike[0].ToString()), Enrollment(lookalike[0].ToString())]);

        var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

        stored.Should().Be(3);
        predicate.Compiled.Should().BeTrue();

        selected.Should().HaveCount(1, "the lookalike satisfies the comparison and the caller's rule for classes, and is not a class");
        selected.Should().Equal(expected[0]);
        allowed.Should().HaveCount(1);
        allowed.Should().Equal(expected[0]);
    }

    [Fact]
    public async Task A_condition_with_no_operator_selects_nothing()
    {
        var (classes, enrollments) = await TypesAsync();

        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission
            {
                ContentTypeSlug = enrollments,
                Read = new PermissionRule
                {
                    Enabled = true,
                    Conditions = new Dictionary<string, object> { [Follows] = new Dictionary<string, object>() },
                },
            });

        // A class the caller may read, holding the field: everything an empty comparison would match.
        var open = await StoreAsync(classes, [Class(caller.Id)]);
        var seeded = await StoreAsync(enrollments, [Enrollment(open[0].ToString()), Enrollment(open[0].ToString())]);
        seeded.Should().HaveCount(2);

        var (predicate, selected, allowed, _) = await AskAsync(caller.Id, enrollments);

        predicate.Compiled.Should().BeTrue();
        selected.Should().BeEmpty("no operator is no comparison, and it must not read as one that always holds");
        allowed.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_conditions_on_one_rule_select_what_the_per_entry_check_allows()
    {
        var (classes, enrollments) = await TypesAsync();

        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission
            {
                ContentTypeSlug = enrollments,
                Read = new PermissionRule
                {
                    Enabled = true,
                    Conditions = new Dictionary<string, object>
                    {
                        [Follows] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        ["Class.Title"] = new Dictionary<string, object> { ["_eq"] = "algebra" },
                    },
                },
            });

        var myAlgebra = await StoreAsync(classes, [Class(caller.Id, "algebra")]);
        var myBiology = await StoreAsync(classes, [Class(caller.Id, "biology")]);
        var theirAlgebra = await StoreAsync(classes, [Class(Guid.NewGuid(), "algebra")]);

        var expected = await StoreAsync(enrollments,
            [Enrollment(myAlgebra[0].ToString()), Enrollment(myAlgebra[0].ToString())]);
        await StoreAsync(enrollments,
            [Enrollment(myBiology[0].ToString()), Enrollment(theirAlgebra[0].ToString()), Enrollment(null)]);

        var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

        stored.Should().Be(5);
        predicate.Compiled.Should().BeTrue();
        predicate.Sql!.Count(ch => ch == '?').Should().Be(predicate.Parameters.Length);

        allowed.Should().HaveCount(2);
        allowed.Should().BeEquivalentTo(expected);
        selected.Should().HaveCount(2);
        selected.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task A_read_rule_on_the_referenced_type_that_the_database_cannot_answer_still_gives_a_predicate()
    {
        var (classes, enrollments) = await TypesAsync();

        // $status is a condition the compiler declines. The caller reads published classes only.
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = Where("$status", "_eq", "Published") },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(Follows, "_eq", "$CURRENT_USER") });

        var published = await StoreAsync(classes, [Class(caller.Id)]);
        var draft = await StoreAsync(classes, [Class(caller.Id)], status: ContentStatus.Draft);
        var theirs = await StoreAsync(classes, [Class(Guid.NewGuid())]);

        var expected = await StoreAsync(enrollments,
            [Enrollment(published[0].ToString()), Enrollment(published[0].ToString())]);
        await StoreAsync(enrollments, [Enrollment(draft[0].ToString()), Enrollment(theirs[0].ToString())]);

        var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

        stored.Should().Be(4);
        predicate.Compiled.Should().BeTrue("the list still pages in the database, from ids the read rule was asked about in memory");

        allowed.Should().HaveCount(2);
        allowed.Should().BeEquivalentTo(expected);
        selected.Should().HaveCount(2);
        selected.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task A_path_that_cannot_resolve_compiles_to_no_rows()
    {
        var (classes, enrollments) = await TypesAsync();

        foreach (var key in new[]
                 {
                     "Student.InstructorUser",        // not a reference
                     "Missing.InstructorUser",        // not a field
                     "Class.Missing",                 // not a field of the referenced type
                     "Class.Teacher.InstructorUser",  // two hops
                 })
        {
            var caller = await CallerAsync(
                new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
                new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(key, "_ne", "nobody") });

            var taught = await StoreAsync(classes, [Class(caller.Id)]);
            var seeded = await StoreAsync(enrollments, [Enrollment(taught[0].ToString())]);

            var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, enrollments);

            stored.Should().BeGreaterThan(0, "the rule has to have something to refuse");
            seeded.Should().HaveCount(1);
            predicate.Compiled.Should().BeTrue("{0} denies every row, and that needs no per-entry pass", key);
            selected.Should().BeEmpty("the rule is {0}", key);
            allowed.Should().BeEmpty("the rule is {0}", key);
        }
    }

    [Fact]
    public async Task A_scope_that_writes_a_referenced_entry_reads_it_again()
    {
        var (classes, enrollments) = await TypesAsync();
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where(Follows, "_eq", "$CURRENT_USER") });

        var taught = await StoreAsync(classes, [Class(caller.Id), Class(caller.Id)]);
        var seeded = await StoreAsync(enrollments,
            [Enrollment(taught[0].ToString()), Enrollment(taught[1].ToString())]);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();
        var user = await session.LoadAsync<User>(caller.Id);

        var entries = await session.Query<Content>().Where(c => c.ContentType == enrollments).ToListAsync();
        entries.Should().HaveCount(2);
        var first = entries.Single(e => e.Id == seeded[0]);
        var second = entries.Single(e => e.Id == seeded[1]);

        // The first check loads the class. The second points elsewhere, which resolves the
        // condition to a set, so both ways of keeping an answer are in play.
        (await resolver.CanPerformActionAsync(user!, enrollments, "read", first)).Should().BeTrue();
        (await resolver.CanPerformActionAsync(user!, enrollments, "read", second)).Should().BeTrue();

        // The first class changes hands, written through this scope's own session.
        var changed = await session.LoadAsync<Content>(taught[0]);
        changed!.Data["InstructorUser"] = Guid.NewGuid().ToString();
        session.Store(changed);
        await session.SaveChangesAsync();

        (await resolver.CanPerformActionAsync(user!, enrollments, "read", first)).Should().BeFalse(
            "what the scope wrote is read again, not answered from before the write");
        (await resolver.CanPerformActionAsync(user!, enrollments, "read", second)).Should().BeTrue();
    }

    [Fact]
    public async Task A_rule_that_follows_nothing_compiles_as_it_did()
    {
        var (classes, _) = await TypesAsync();
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = Where("InstructorUser", "_eq", "$CURRENT_USER") });

        var mine = await StoreAsync(classes, [Class(caller.Id)]);
        await StoreAsync(classes, [Class(Guid.NewGuid())]);

        var (predicate, selected, allowed, stored) = await AskAsync(caller.Id, classes);

        stored.Should().Be(2);
        predicate.Should().BeEquivalentTo(PermissionPredicateCompiler.Compile(
            [Where("InstructorUser", "_eq", "$CURRENT_USER")], caller.Id));
        selected.Should().Equal(mine[0]);
        allowed.Should().Equal(mine[0]);
    }
}
