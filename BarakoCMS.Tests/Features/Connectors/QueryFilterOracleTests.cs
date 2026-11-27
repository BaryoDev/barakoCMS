using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Which rows come back is a fact about every field the query uses, not only the ones it returns,
/// so a filter or a sort on a field the reader may not see, or a row the reader may not see at all,
/// never shows in the rows or the count.
/// </summary>
[Collection("Integration1")]
public class QueryFilterOracleTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(string Type, IServiceScope Scope, Content Open, Content Sensitive, Content Hidden);

    private async Task<Setup> SeedAsync()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var type = TypeName();
        var open = Entry(type, new() { ["Name"] = "Ana", ["Salary"] = 150000m, ["Level"] = "a", ["Owned"] = "o" });
        var sensitive = Entry(type, new() { ["Name"] = "Ben", ["Salary"] = 150000m, ["Level"] = "b", ["Owned"] = "o" });
        var hidden = Entry(type, new() { ["Name"] = "Cai", ["Salary"] = 150000m, ["Level"] = "c", ["Owned"] = "o" });
        sensitive.Sensitivity = SensitivityLevel.Sensitive;
        hidden.Sensitivity = SensitivityLevel.Hidden;

        await QueryKit.SeedAsync(
            store, null,
            Type(type, Field("Name"), Field("Salary", "decimal"), Field("Level", "text", SensitivityLevel.Sensitive),
                new FieldDefinition { Name = "Owned", DisplayName = "Owned", Type = "text", ShownWhenListedIn = "Shown" }),
            [open, sensitive, hidden], Ct);
        return new Setup(type, factory.Services.CreateScope(), open, sensitive, hidden);
    }

    private static QueryDefinition Query(string type, params string[] select) =>
        new() { ContentType = type, Fields = [.. select], Limit = 10 };

    [Fact]
    public async Task A_filter_on_an_owner_hidden_field_is_refused_even_when_no_selected_field_is_hidden()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var definition = Query(s.Type, "Name");
        definition.Filters = [Where("Owned", "eq", "o")];

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(definition, AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
        result.Rows.Should().BeEmpty();
        result.Refusal.Should().Contain("Owned");
    }

    [Fact]
    public async Task A_sort_on_a_field_above_the_users_sensitivity_is_refused()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var definition = Query(s.Type, "Name");
        definition.SortField = "Level";

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(definition, AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("Level");
    }

    [Fact]
    public async Task A_filter_on_a_field_above_the_users_sensitivity_is_refused()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var definition = Query(s.Type, "Name");
        definition.Filters = [Where("Level", "eq", "b")];

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(definition, AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("Level");
    }

    [Fact]
    public async Task An_entry_the_user_may_not_see_at_all_never_appears_in_the_rows_or_the_count()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (s.Type, null));
        var runner = s.Scope.ServiceProvider.GetRequiredService<QueryRunner>();

        // Each entry matches this filter. Only the first may be seen, and the answer must not say
        // that the others are there.
        var byFilter = Query(s.Type, "Name");
        byFilter.Filters = [Where("Salary", "gt", "100000")];
        var found = await runner.RunAsync(byFilter, AsActor(actor), Ct);
        found.Ok.Should().BeTrue(found.Refusal);
        found.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
        found.Count.Should().Be(1);

        // A query that returns nothing but ids and a status has no field to trip on, so the entry
        // itself has to be left out.
        var byStatus = Query(s.Type, "id");
        byStatus.Filters = [Where("status", "eq", "Published")];
        var ids = await runner.RunAsync(byStatus, AsActor(actor), Ct);
        ids.Ok.Should().BeTrue(ids.Refusal);
        ids.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
        ids.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task No_user_sees_only_the_entries_that_are_public_as_a_whole()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var byStatus = Query(s.Type, "id");
        byStatus.Filters = [Where("status", "eq", "Published")];

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(byStatus, AsActor(Guid.Empty), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().ContainSingle().Which.Should().Be(s.Open.Id);
    }
}
