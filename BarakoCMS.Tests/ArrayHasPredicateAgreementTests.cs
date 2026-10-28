// ArrayHasPredicateAgreementTests.cs: the property PermissionPredicateAgreementTests holds, for _has.
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class ArrayHasPredicateAgreementTests
{
    private readonly IntegrationTestFixture _fixture;
    public ArrayHasPredicateAgreementTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static readonly Guid Caller = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Dictionary<string, object> Rule(string expected) =>
        new() { ["VisibleToRoles"] = new Dictionary<string, object> { ["_has"] = expected } };

    // name, the entry's data, whether the evaluator and the predicate must both select it for "Member"
    private static readonly (string Name, Dictionary<string, object> Data, bool Selected)[] Shapes =
    [
        ("lists Member",            new() { ["VisibleToRoles"] = new List<object> { "Member" } }, true),
        ("lists Member and Auditor", new() { ["VisibleToRoles"] = new List<object> { "Member", "Auditor" } }, true),
        ("lists only Auditor",      new() { ["VisibleToRoles"] = new List<object> { "Auditor" } }, false),
        ("empty array",             new() { ["VisibleToRoles"] = new List<object>() }, false),
        ("field absent",            new(), false),
        ("a string, not an array",  new() { ["VisibleToRoles"] = "Member" }, false),
        ("a string holding Member inside it", new() { ["VisibleToRoles"] = "Member,Auditor" }, false),
        ("different case",          new() { ["VisibleToRoles"] = new List<object> { "member" } }, false),
        ("an array of numbers",     new() { ["VisibleToRoles"] = new List<object> { 1, 2 } }, false),
        ("a nested array",          new() { ["VisibleToRoles"] = new List<object> { new List<object> { "Member" } } }, false),
        ("a mixed array with Member", new() { ["VisibleToRoles"] = new List<object> { 1, "Member", null! } }, true),
        ("a string that looks like an array", new() { ["VisibleToRoles"] = "[\"Member\"]" }, false),
        ("an array of objects",     new() { ["VisibleToRoles"] = new List<object> { new Dictionary<string, object> { ["r"] = "Member" } } }, false),
    ];

    [Fact]
    public async Task The_compiled_predicate_selects_exactly_what_the_evaluator_allows_and_only_arrays_that_list_the_value()
    {
        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };
        var type = "has" + Guid.NewGuid().ToString("N")[..8];
        var ids = new Dictionary<string, Guid>();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var (name, data, _) in Shapes)
            {
                var content = new Content
                {
                    Id = Guid.NewGuid(), ContentType = type, Data = data,
                    CreatedBy = Caller, LastModifiedBy = Caller, Status = ContentStatus.Draft,
                };
                ids[name] = content.Id;
                session.Store(content);
            }
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var conditions = Rule("Member");
        var predicate = PermissionPredicateCompiler.Compile([new PermissionRule { Enabled = true, Conditions = conditions }], user.Id);
        predicate.Compiled.Should().BeTrue("a plain string expected value has to compile, or this test proves nothing");

        using var read = _fixture.Services.CreateScope();
        var query = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync(TestContext.Current.CancellationToken);
        stored.Should().HaveCount(Shapes.Length);

        // Read back from storage: the evaluator then sees JsonElement, as it does in production.
        var byEvaluator = stored.Where(c => new ConditionEvaluator().Evaluate(conditions, c, user)).Select(c => c.Id).Order().ToList();
        var bySql = (await query.Query<Content>()
                .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                .ToListAsync(TestContext.Current.CancellationToken))
            .Select(c => c.Id).Order().ToList();

        var wanted = Shapes.Where(x => x.Selected).Select(x => ids[x.Name]).Order().ToList();
        wanted.Should().HaveCount(3);
        byEvaluator.Should().Equal(wanted, "the evaluator selects arrays that list the value and nothing else");
        bySql.Should().Equal(byEvaluator, "the predicate and the evaluator must select the same rows");
    }

    [Fact]
    public void A_variable_as_the_expected_value_is_declined_so_the_evaluator_decides()
    {
        PermissionPredicateCompiler.Compile(
            [new PermissionRule { Enabled = true, Conditions = Rule("$CURRENT_USER") }], Caller).Compiled.Should().BeFalse();
    }

    [Fact]
    public async Task A_caller_attribute_reference_as_the_expected_value_selects_nothing_in_the_predicate_and_the_evaluator()
    {
        var conditions = Rule("$CURRENT_USER.branch");
        var predicate = PermissionPredicateCompiler.Compile(
            [new PermissionRule { Enabled = true, Conditions = conditions }], Caller);

        var user = new User { Id = Caller, Username = "caller", Email = "caller@example.com" };
        var type = "has" + Guid.NewGuid().ToString("N")[..8];
        var literal = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, CreatedBy = Caller, LastModifiedBy = Caller,
            Data = new() { ["VisibleToRoles"] = new List<object> { "$CURRENT_USER.branch" } },
        };
        var other = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, CreatedBy = Caller, LastModifiedBy = Caller,
            Data = new() { ["VisibleToRoles"] = new List<object> { "Manila" } },
        };

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(literal, other);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var read = _fixture.Services.CreateScope();
        var query = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync(TestContext.Current.CancellationToken);
        stored.Where(c => new ConditionEvaluator().Evaluate(conditions, c, user)).Should().BeEmpty(
            "the evaluator refuses a variable as a _has value, even when an entry lists that exact text");

        if (predicate.Compiled)
        {
            var bySql = await query.Query<Content>()
                .Where(c => c.ContentType == type && c.MatchesSql(predicate.Sql!, predicate.Parameters))
                .ToListAsync(TestContext.Current.CancellationToken);
            bySql.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_document_property_key_is_declined()
    {
        var conditions = new Dictionary<string, object> { ["$createdBy"] = new Dictionary<string, object> { ["_has"] = "x" } };
        PermissionPredicateCompiler.Compile([new PermissionRule { Enabled = true, Conditions = conditions }], Caller).Compiled.Should().BeFalse();
    }

    [Fact]
    public void A_status_key_with_a_list_operator_is_declined()
    {
        var conditions = new Dictionary<string, object> { ["$status"] = new Dictionary<string, object> { ["_has"] = "Published" } };
        PermissionPredicateCompiler.Compile([new PermissionRule { Enabled = true, Conditions = conditions }], Caller).Compiled.Should().BeFalse();
    }
}
