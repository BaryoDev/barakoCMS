using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>id, status, createdAt and updatedAt can be selected, sorted on and filtered on in a step's run.</summary>
[Collection("Integration1")]
public class QuerySystemFieldTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Day = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private async Task<(string Type, IQuerySession Session, Content[] Rows)> SeedAsync()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        var rows = new[]
        {
            Entry(type, new() { ["Name"] = "a" }, Day, ContentStatus.Published, new Guid("00000000-0000-0000-0000-00000000000b")),
            Entry(type, new() { ["Name"] = "b" }, Day, ContentStatus.Draft, new Guid("00000000-0000-0000-0000-00000000000a")),
            Entry(type, new() { ["Name"] = "c" }, Day.AddDays(1), ContentStatus.Published, new Guid("00000000-0000-0000-0000-00000000000c")),
        };
        await QueryKit.SeedAsync(store, tenant, Type(type, Field("Name")), rows, Ct);
        return (type, store.QuerySession(tenant), rows);
    }

    private static Task<QueryResult> RunAsync(IQuerySession session, QueryDefinition definition, params (string, string)[] values) =>
        ((IContextQueryRunner)new QueryRunner(session)).RunAsync(definition, new QueryRunContext(Values(values)), Ct);

    [Fact]
    public async Task Select_returns_the_system_fields_in_a_stable_form()
    {
        var (type, session, rows) = await SeedAsync();
        await using var _ = session;

        var result = await RunAsync(session, new QueryDefinition
        {
            ContentType = type, Fields = ["id", "status", "createdAt", "Name"], SortField = "createdAt", Limit = 1,
            Filters = [Where("status", "eq", "draft")],
        });

        result.Ok.Should().BeTrue(result.Refusal);
        result.Rows.Should().ContainSingle();
        result.Rows[0]["id"].Should().Be(rows[1].Id.ToString());
        result.Rows[0]["status"].Should().Be("Draft");
        result.Rows[0]["createdAt"].Should().Be(Day.ToString("O"));
        result.Rows[0]["Name"].Should().Be("b");
    }

    [Fact]
    public async Task Order_by_a_system_field_ends_on_the_id()
    {
        var (type, session, rows) = await SeedAsync();
        await using var _ = session;

        var asc = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], SortField = "createdAt" });
        asc.Ids.Should().Equal(rows[1].Id, rows[0].Id, rows[2].Id);

        var desc = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], SortField = "createdAt", Descending = true });
        desc.Ids.Should().Equal(rows[2].Id, rows[1].Id, rows[0].Id);

        var byId = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], SortField = "id" });
        byId.Ids.Should().Equal(rows[1].Id, rows[0].Id, rows[2].Id);
    }

    [Fact]
    public async Task Filters_on_status_id_and_dates_match_what_they_say()
    {
        var (type, session, rows) = await SeedAsync();
        await using var _ = session;

        (await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [Where("status", "ne", "Published")] }))
            .Ids.Should().Equal(rows[1].Id);
        (await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [WhereParam("id", "eq", "id")] }, ("id", rows[2].Id.ToString())))
            .Ids.Should().Equal(rows[2].Id);
        (await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [Where("createdAt", "gt", Day.ToString("O"))] }))
            .Ids.Should().Equal(rows[2].Id);
        (await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], SortField = "id", Filters = [Where("createdAt", "lte", Day.ToString("O"))] }))
            .Ids.Should().Equal(rows[1].Id, rows[0].Id);
    }

    [Theory]
    [InlineData("status", "eq", "1", "not a status")]
    [InlineData("status", "eq", "nope", "not a status")]
    [InlineData("status", "lt", "Draft", "does not apply")]
    [InlineData("id", "contains", "a", "does not apply")]
    [InlineData("id", "eq", "not-an-id", "not an id")]
    [InlineData("createdAt", "eq", "yesterday-ish", "not a date")]
    public async Task A_bad_system_filter_is_refused_with_a_message(string field, string op, string value, string expected)
    {
        var (type, session, _) = await SeedAsync();
        await using var _s = session;

        var result = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [Where(field, op, value)] });

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain(expected).And.NotContain(value.Length > 3 ? value : "\u0000");
    }

    [Fact]
    public async Task The_old_run_and_validation_keep_refusing_system_names()
    {
        var (type, session, _) = await SeedAsync();
        await using var _s = session;
        var runner = new QueryRunner(session);

        foreach (var definition in new[]
        {
            new QueryDefinition { ContentType = type, Fields = ["id"] },
            new QueryDefinition { ContentType = type, Fields = ["Name"], SortField = "createdAt" },
            new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [Where("status", "eq", "Draft")] },
        })
        {
            (await runner.RunAsync(definition, Ct)).Ok.Should().BeFalse("a reader that never gave system fields a meaning keeps its rule");
        }
    }
}
