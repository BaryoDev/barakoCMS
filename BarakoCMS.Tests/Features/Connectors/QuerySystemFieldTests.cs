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

    [Theory]
    [InlineData("Published,Archived")]
    [InlineData("Draft,Published")]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("Published ,")]
    [InlineData("")]
    public async Task A_status_filter_takes_one_status_name_and_nothing_else(string value)
    {
        var (type, session, _) = await SeedAsync();
        await using var _s = session;

        var result = await RunAsync(session, new QueryDefinition
        {
            ContentType = type, Fields = ["Name"], Filters = [WhereParam("status", "eq", "wanted")],
        }, ("wanted", value));

        result.Ok.Should().BeFalse("a comma list is read as flags and a number as a value, and neither is a status");
        result.Refusal.Should().Contain("not a status");
    }

    [Theory]
    [InlineData("published")]
    [InlineData("PUBLISHED")]
    [InlineData(" Published ")]
    public async Task A_status_name_is_read_in_any_case(string value)
    {
        var (type, session, rows) = await SeedAsync();
        await using var _s = session;

        var result = await RunAsync(session, new QueryDefinition
        {
            ContentType = type, Fields = ["Name"], Filters = [WhereParam("status", "eq", "wanted")],
        }, ("wanted", value));

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().BeEquivalentTo([rows[0].Id, rows[2].Id]);
    }

    [Fact]
    public async Task A_field_the_type_declares_wins_over_the_system_field_of_the_same_name()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var type = TypeName();
        var open = Entry(type, new() { ["Status"] = "open", ["Name"] = "a" });
        var closed = Entry(type, new() { ["Status"] = "closed", ["Name"] = "b" });
        await QueryKit.SeedAsync(store, null, Type(type, Field("Status"), Field("Name")), [open, closed], Ct);
        var actor = await ActorAsync(store, Ct, (type, null));

        var definition = new QueryDefinition
        {
            ContentType = type, Fields = ["status", "Name"], SortField = "status", Filters = [Where("status", "eq", "open")],
        };

        // Both ways a query is read: with no one behind it, and as a user.
        using var scope = factory.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<QueryRunner>();
        foreach (var context in new[] { new QueryRunContext(null), AsActor(actor) })
        {
            var result = await runner.RunAsync(definition, context, Ct);

            result.Ok.Should().BeTrue(result.Refusal);
            result.Ids.Should().Equal(new[] { open.Id }, "the filter reads the data field, which is open on one entry; the entry status is Published on both");
            result.Rows[0]["status"].Should().Be("open", "the field is the type's, not Content.Status");
        }
    }

    [Fact]
    public async Task A_run_refuses_more_than_fifty_terms_and_a_null_in_a_list()
    {
        var (type, session, _) = await SeedAsync();
        await using var _s = session;

        var wide = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = [.. Enumerable.Repeat("Name", 51)] });
        wide.Ok.Should().BeFalse();
        wide.Refusal.Should().Contain("at most 50 fields, filters or measures");

        var nullField = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name", null!] });
        nullField.Ok.Should().BeFalse("a null in a list is a refusal, not an exception");

        var nullFilter = await RunAsync(session, new QueryDefinition { ContentType = type, Fields = ["Name"], Filters = [null!] });
        nullFilter.Ok.Should().BeFalse();
    }
}
