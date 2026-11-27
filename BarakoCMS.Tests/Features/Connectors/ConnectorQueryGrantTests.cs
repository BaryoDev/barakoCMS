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

    [Fact]
    public async Task A_followed_reference_to_a_hidden_entry_sends_the_row_without_its_value()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (_, products) => [products + ".Cost"]);
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var product = await session.Query<Content>().Where(c => c.ContentType == s.Products).SingleAsync(Ct);
            product.Sensitivity = SensitivityLevel.Hidden;
            session.Store(product);
            await session.SaveChangesAsync(Ct);
        }

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Product.Cost"] });

        composed.Ok.Should().BeTrue("got: {0}", composed.Refusal);
        composed.Body.Should().Contain("Qty").And.NotContain(CostValue, "a grant on the type does not open an entry Hidden by its own level");
    }

    [Fact]
    public async Task A_query_judges_its_fields_by_the_account_definition_and_not_by_a_looser_tenant_copy()
    {
        var subject = await GrantKit.SeedAsync(fixture, []);
        var type = GrantKit.Name("qga");
        FieldDefinition Ssn(SensitivityLevel level) =>
            new() { Name = "Ssn", DisplayName = "Ssn", Type = "string", Sensitivity = level };
        await GrantKit.StoreAsync(
            fixture,
            new AccountContentType
            {
                Id = Guid.NewGuid(), AccountId = TenantAccount.DefaultId, Name = type,
                Definition = new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Ssn(SensitivityLevel.Sensitive)],
                },
            },
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Ssn(SensitivityLevel.Public)],
            },
            new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Data = new() { ["Ssn"] = SsnValue } });
        var query = new QueryDefinition
        {
            Id = Guid.NewGuid(), Slug = GrantKit.Name("qgq"), Name = "Account query", ContentType = type, Fields = ["Ssn"],
        };
        await GrantKit.StoreAsync(fixture, query);
        var definition = RequestKit.Definition(subject, "{{query.rows}}");
        definition.QuerySlug = query.Slug;

        var composed = await RequestKit.ComposeAsync(fixture, subject, definition);

        composed.Ok.Should().BeFalse("the account's Sensitive Ssn decides, not the tenant's copy");
        (composed.Body ?? string.Empty).Should().NotContain(SsnValue);
        (composed.Refusal ?? string.Empty).Should().NotContain(SsnValue);
    }

    [Fact]
    public async Task A_followed_reference_is_judged_by_the_account_definition_of_the_type_it_points_at()
    {
        var s = await GrantKit.SeedLinesAsync(fixture, (lines, _) => [lines + ".Note"]);
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var copy = await session.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == s.Products, Ct);
            var account = new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = s.Products, DisplayName = s.Products,
                Fields = [.. copy.Fields.Select(f => new FieldDefinition
                {
                    Name = f.Name, DisplayName = f.DisplayName, Type = f.Type, Sensitivity = f.Sensitivity,
                })],
            };
            copy.Fields.Single(f => f.Name == "Cost").Sensitivity = SensitivityLevel.Public;
            session.Store(copy);
            session.Store(new AccountContentType
            {
                Id = Guid.NewGuid(), AccountId = TenantAccount.DefaultId, Name = s.Products, Definition = account,
            });
            await session.SaveChangesAsync(Ct);
        }

        var composed = await ComposeAsync(s, new QueryDefinition { Fields = ["Qty", "Product.Cost"] });

        composed.Ok.Should().BeFalse("the account's Sensitive Cost decides, and it is not granted");
        (composed.Body ?? string.Empty).Should().NotContain(CostValue);
        (composed.Refusal ?? string.Empty).Should().NotContain(CostValue);
    }
}
