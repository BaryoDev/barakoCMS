using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Whether a run is refused is decided from the schema and the reader's rights alone, so it cannot
/// depend on which rows match. An entry the reader may not see at all is left out before the limit.
/// </summary>
[Collection("Integration1")]
public class QueryFilterOracleTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(string Type, IServiceScope Scope, Content Open, Content Sensitive, Content Hidden);

    /// <summary>
    /// Three entries, the hidden one newest, so the default order puts it first. Salary is one the
    /// owner can hide, Level is Sensitive.
    /// </summary>
    private async Task<Setup> SeedAsync()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var type = TypeName();
        var now = DateTime.UtcNow;
        var open = Entry(type, new() { ["Name"] = "Ana", ["Salary"] = 150000m, ["Level"] = "a" }, now.AddMinutes(-3));
        var sensitive = Entry(type, new() { ["Name"] = "Ben", ["Salary"] = 200000m, ["Level"] = "b" }, now.AddMinutes(-2));
        var hidden = Entry(type, new() { ["Name"] = "Cai", ["Salary"] = 250000m, ["Level"] = "c" }, now.AddMinutes(-1));
        sensitive.Sensitivity = SensitivityLevel.Sensitive;
        hidden.Sensitivity = SensitivityLevel.Hidden;

        await QueryKit.SeedAsync(
            store, null,
            Type(type, Field("Name"), Field("Level", "text", SensitivityLevel.Sensitive),
                new FieldDefinition { Name = "Salary", DisplayName = "Salary", Type = "decimal", ShownWhenListedIn = "Shown" }),
            [open, sensitive, hidden], Ct);
        return new Setup(type, factory.Services.CreateScope(), open, sensitive, hidden);
    }

    private static QueryDefinition Query(string type, params string[] select) =>
        new() { ContentType = type, Fields = [.. select], Limit = 10 };

    /// <summary>A user who reads the type with the view_sensitive capability, which is what exempts a reader from owner hiding.</summary>
    private async Task<Guid> ExemptActorAsync(string type)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "qx" + Guid.NewGuid().ToString("n")[..10],
            SystemCapabilities = [SystemCapabilities.ViewSensitive],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = role.Name + "@example.com", RoleIds = [role.Id] };

        await using var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user.Id;
    }

    private async Task<QueryResult> RunAsync(Setup s, Guid actor, QueryDefinition definition) =>
        await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(definition, AsActor(actor), Ct);

    [Fact]
    public async Task A_filter_on_an_owner_hidden_field_is_refused_the_same_whether_no_row_one_row_or_many_match()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));

        var refusals = new List<string?>();
        foreach (var (op, value) in new[] { ("gt", "999999"), ("eq", "150000"), ("gt", "100000") })
        {
            var definition = Query(s.Type, "Name");
            definition.Filters = [Where("Salary", op, value)];
            var result = await RunAsync(s, actor, definition);

            result.Ok.Should().BeFalse();
            result.Rows.Should().BeEmpty();
            refusals.Add(result.Refusal);
        }

        refusals[0].Should().Contain("Salary");
        refusals.Distinct().Should().ContainSingle("the answer cannot depend on what matched");
    }

    [Fact]
    public async Task A_filter_that_also_names_a_readable_field_is_refused_whatever_the_readable_field_matches()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));

        var refusals = new List<string?>();
        foreach (var name in new[] { "Ana", "Ben", "Nobody" })
        {
            var definition = Query(s.Type, "Name");
            definition.Filters = [Where("Salary", "gt", "100000"), Where("Name", "eq", name)];
            refusals.Add((await RunAsync(s, actor, definition)).Refusal);
        }

        refusals.Should().OnlyContain(r => r != null);
        refusals.Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task A_sort_on_a_field_above_the_users_sensitivity_is_refused_before_any_row_is_read()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var definition = Query(s.Type, "Name");
        definition.SortField = "Level";

        var withRows = await RunAsync(s, actor, definition);

        // The same answer once there is nothing to read, so the entries had no say in it.
        await using (var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.DeleteWhere<Content>(c => c.ContentType == s.Type);
            await session.SaveChangesAsync(Ct);
        }

        var withoutRows = await RunAsync(s, actor, definition);

        withRows.Ok.Should().BeFalse();
        withRows.Refusal.Should().Contain("Level");
        withoutRows.Refusal.Should().Be(withRows.Refusal);
    }

    [Fact]
    public async Task A_filter_on_a_field_above_the_users_sensitivity_is_refused()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var definition = Query(s.Type, "Name");
        definition.Filters = [Where("Level", "eq", "b")];

        var result = await RunAsync(s, actor, definition);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("Level");
    }

    [Fact]
    public async Task An_entry_above_the_users_document_sensitivity_never_appears_and_limit_one_returns_the_first_visible_match()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));

        // Every entry matches. The newest is hidden as a whole and the next is Sensitive, so the
        // first one this reader may see is the oldest.
        var all = Query(s.Type, "Name");
        all.Filters = [Where("status", "eq", "Published")];
        var found = await RunAsync(s, actor, all);
        found.Ok.Should().BeTrue(found.Refusal);
        found.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
        found.Count.Should().Be(1);

        var first = Query(s.Type, "Name");
        first.Limit = 1;
        var one = await RunAsync(s, actor, first);
        one.Ok.Should().BeTrue(one.Refusal);
        one.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
        one.Rows.Should().ContainSingle().Which["Name"].Should().Be("Ana");
    }

    [Fact]
    public async Task No_user_sees_only_the_entries_that_are_public_as_a_whole()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var byStatus = Query(s.Type, "id");
        byStatus.Filters = [Where("status", "eq", "Published")];

        var result = await RunAsync(s, Guid.Empty, byStatus);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
    }

    [Fact]
    public async Task A_user_who_may_read_the_field_on_every_entry_runs_the_same_query_and_gets_the_rows()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ExemptActorAsync(s.Type);
        var definition = Query(s.Type, "Name", "Salary");
        definition.Filters = [Where("Salary", "gt", "100000")];
        definition.SortField = "Salary";

        var result = await RunAsync(s, actor, definition);

        // The hidden entry is still out of sight: the capability that frees a field does not free a document.
        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().Equal(s.Open.Id, s.Sensitive.Id);
        result.Rows.Select(r => r["Name"]).Should().Equal("Ana", "Ben");
        result.Rows.Select(r => Convert.ToDecimal(r["Salary"])).Should().Equal(150000m, 200000m);
    }
}
