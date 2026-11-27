using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>A number filter compares as a decimal, so the value written is the value matched.</summary>
[Collection("Integration1")]
public class QueryNumberTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(QueryRunner Runner, string Type, Guid Exact, IQuerySession Session)> SeedAsync(decimal amount, params decimal[] near)
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        var exact = Entry(type, new() { ["Amount"] = amount });
        await QueryKit.SeedAsync(store, tenant, Type(type, Field("Amount", "decimal")),
            near.Select(n => Entry(type, new() { ["Amount"] = n })).Append(exact), Ct);

        var session = store.QuerySession(tenant);
        return (new QueryRunner(session), type, exact.Id, session);
    }

    [Theory]
    [InlineData("19.99", "eq")]
    [InlineData("12345678901234567", "eq")]
    public async Task A_bound_number_returns_the_exact_row(string text, string op)
    {
        var amount = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var (runner, type, exact, session) = await SeedAsync(amount, 19.98m, 20m, 12345678901234566m, 12345678901234568m, 1999m);
        await using var _ = session;

        var result = await ((IContextQueryRunner)runner).RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Amount"], Filters = [WhereParam("Amount", op, "data.Amount")] },
            new QueryRunContext(Values(("data.Amount", text))), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().Equal(exact);
    }

    [Fact]
    public async Task A_17_digit_value_is_not_rounded_to_its_neighbour()
    {
        var (runner, type, exact, session) = await SeedAsync(12345678901234567m, 12345678901234568m, 12345678901234566m);
        await using var _ = session;

        var result = await ((IContextQueryRunner)runner).RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Amount"], Filters = [WhereParam("Amount", "gte", "data.Amount")], SortField = "Amount" },
            new QueryRunContext(Values(("data.Amount", "12345678901234567"))), Ct);

        result.Rows.Should().HaveCount(2, "the stored value and the one above it, not the one below");
        result.Ids.First().Should().Be(exact);
    }

    [Fact]
    public async Task A_literal_number_filter_compares_as_a_decimal_too()
    {
        var (runner, type, exact, session) = await SeedAsync(19.99m, 19.98m, 20m);
        await using var _ = session;

        var result = await runner.RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Amount"], Filters = [Where("Amount", "eq", "19.99")] }, Ct);

        result.Ids.Should().Equal(exact);
    }

    [Theory]
    [InlineData("1,999")]
    [InlineData("$19.99")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task A_parameter_that_is_not_a_plain_decimal_is_refused(string text)
    {
        var (runner, type, _, session) = await SeedAsync(1m);
        await using var _s = session;

        var result = await ((IContextQueryRunner)runner).RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Amount"], Filters = [WhereParam("Amount", "eq", "data.Amount")] },
            new QueryRunContext(Values(("data.Amount", text))), Ct);

        result.Ok.Should().BeFalse();
    }
}
