using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static BarakoCMS.Tests.Features.Connectors.GrantKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// The rows a request's query adds follow the connector's grants: a granted field may be projected,
/// filtered and sorted on, and a followed reference field needs a grant on the type it points at.
/// </summary>
[Collection("Integration1")]
public class ConnectorQueryGrantTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ComposedRequest> ComposeAsync(GrantKit.LineSeed s, QueryDefinition query)
    {
        query.Id = Guid.NewGuid();
        query.Slug = GrantKit.Name("qgq");
        query.Name = "Grant query";
        query.ContentType = s.Lines;
        await GrantKit.StoreAsync(fixture, query);

        var definition = RequestKit.Definition(s.Subject, "{{query.rows}}");
        definition.QuerySlug = query.Slug;
        return await RequestKit.ComposeAsync(fixture, s.Subject, definition);
    }

    [Fact]
    public async Task A_query_projecting_a_granted_field_sends_it()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Note"] });

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Contain(NoteValue);
    }

    [Fact]
    public async Task A_query_projecting_a_field_its_connector_is_not_granted_is_refused()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (_, _) => []);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Note"] });

        composed.Ok.Should().BeFalse();
        composed.Refusal.Should().NotContain(NoteValue);
    }

    [Fact]
    public async Task A_query_filtering_on_a_granted_field_is_allowed()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);

        var composed = await ComposeAsync(s, new QueryDefinition
        {
            Fields = ["Qty"], Filters = [new QueryFilter { Field = "Note", Op = "eq", Value = NoteValue }],
        });

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Contain("2");
    }

    [Fact]
    public async Task A_query_filtering_on_a_field_its_connector_is_not_granted_is_refused()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (_, _) => []);

        var composed = await ComposeAsync(s, new QueryDefinition
        {
            Fields = ["Qty"], Filters = [new QueryFilter { Field = "Note", Op = "eq", Value = NoteValue }],
        });

        composed.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_query_sorting_on_a_granted_field_is_allowed()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty"], SortField = "Note" });

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
    }

    [Fact]
    public async Task A_query_sorting_on_a_field_its_connector_is_not_granted_is_refused()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (_, _) => []);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty"], SortField = "Note" });

        composed.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_followed_reference_field_is_sent_when_it_is_granted_on_the_type_it_points_at()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (_, products) => [products + ".Cost"]);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Product.Cost"] });

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Contain(CostValue);
    }

    [Fact]
    public async Task A_followed_reference_field_granted_only_on_the_referring_type_is_refused()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note", lines + ".Cost"]);

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Product.Cost"] });

        composed.Ok.Should().BeFalse("the grant must name the field on the type it lives on");
        composed.Refusal.Should().NotContain(CostValue);
    }

    [Fact]
    public async Task A_query_row_is_left_out_when_its_entry_is_hidden()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var entry = (await session.LoadAsync<Content>(s.Subject.EntryId, Ct))!;
            entry.Sensitivity = SensitivityLevel.Hidden;
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Note"] });

        composed.Body.Should().NotContain(NoteValue, "a Hidden entry is not sent through a grant");
    }
}
