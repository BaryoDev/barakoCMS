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
        SensitivityLevel sensitivity = SensitivityLevel.Public)
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
                Status = ContentStatus.Published,
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
    /// What the predicate selects of a type and what the per-entry check allows of it, both asked
    /// of one resolver, which is how one request asks them.
    /// </summary>
    private async Task<(ReadPredicate Predicate, List<Guid> Selected, List<Guid> Allowed, int Stored)> AskAsync(
        Guid userId, string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var resolver = scope.ServiceProvider.GetRequiredService<PermissionResolver>();

        var user = await session.LoadAsync<User>(userId);
        user.Should().NotBeNull();

        var predicate = await resolver.ReadPredicateAsync(user!, type);

        var selected = new List<Guid>();
        if (predicate.Compiled)
        {
            selected = (await session.Query<Content>()
                    .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                    .ToListAsync())
                .Select(c => c.Id)
                .ToList();
        }

        // Read back, so the per-entry check sees the shapes a request sees.
        var stored = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync();

        var allowed = new List<Guid>();
        foreach (var entry in stored)
        {
            if (await resolver.CanPerformActionAsync(user!, type, "read", entry))
                allowed.Add(entry.Id);
        }

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
    public async Task A_comparison_the_compiler_declines_leaves_the_rule_to_the_per_entry_check()
    {
        var (classes, enrollments) = await TypesAsync();

        // A number is one of the expected values the compiler declines, on a row or through a
        // reference. The evaluator compares it as text, so the per-entry check still answers.
        var caller = await CallerAsync(
            new ContentTypePermission { ContentTypeSlug = classes, Read = new PermissionRule { Enabled = true } },
            new ContentTypePermission { ContentTypeSlug = enrollments, Read = Where("Class.Seats", "_eq", 30L) });

        var full = await StoreAsync(classes, [Class(Guid.NewGuid())]);
        var seeded = await StoreAsync(enrollments, [Enrollment(full[0].ToString()), Enrollment(null)]);

        var (predicate, _, allowed, _) = await AskAsync(caller.Id, enrollments);

        predicate.Compiled.Should().BeFalse();
        allowed.Should().HaveCount(1);
        allowed.Should().Equal(seeded[0]);
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
