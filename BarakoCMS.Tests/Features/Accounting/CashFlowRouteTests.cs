#pragma warning disable CS0618 // Accounting is obsolete in 5.5 and is removed in #1285
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Accounting.Domain;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>GET /api/accounting/cash-flow reports one month over the listed cash accounts.</summary>
[Collection("Integration1")]
public class CashFlowRouteTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConfigCallers _callers = new(fixture);

    private async Task VoidAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = (await session.LoadAsync<Content>(id, Ct))!;
        var result = await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
            entry, "Void", ContentTransitionActor.ForSystem("accounting"),
            new ContentTransitionOptions
            {
                SkipPermissionChecks = true,
                Data = new Dictionary<string, object> { ["VoidReason"] = "wrong account" },
            }, Ct);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Seeded_postings_give_receipts_disbursements_opening_and_closing_and_a_voided_entry_stays_in()
    {
        var cashA = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var cashB = await AccountingTestData.AccountAsync(fixture, "1020", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var expense = await AccountingTestData.AccountAsync(fixture, "5010", AccountType.Expense);
        await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 9, 20), cashA, income, 100m);
        await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 10, 5), cashA, income, 50m);
        await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 10, 9), expense, cashB, 30m);
        var wrong = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 10, 12), cashA, income, 999m);
        await VoidAsync(wrong);
        var client = await _callers.SuperAdminAsync(null);

        var response = await client.GetAsync($"/api/accounting/cash-flow?year=2026&month=10&cash={cashA},{cashB}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("period").GetString().Should().Be("2026-10");
        body.GetProperty("opening").GetDecimal().Should().Be(100m);
        body.GetProperty("receipts").GetDecimal().Should().Be(1049m);
        body.GetProperty("disbursements").GetDecimal().Should().Be(30m);
        body.GetProperty("closing").GetDecimal().Should().Be(1119m);
    }

    [Theory]
    [InlineData("month=13")]
    [InlineData("month=0")]
    public async Task A_month_outside_1_to_12_is_a_400(string month)
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var client = await _callers.SuperAdminAsync(null);

        (await client.GetAsync($"/api/accounting/cash-flow?year=2026&{month}&cash={cash}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_empty_cash_list_is_a_400()
    {
        var client = await _callers.SuperAdminAsync(null);

        (await client.GetAsync("/api/accounting/cash-flow?year=2026&month=10&cash=", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task More_than_20_codes_is_a_400()
    {
        var client = await _callers.SuperAdminAsync(null);
        var codes = string.Join(',', Enumerable.Range(1, 21).Select(i => $"c{i}"));

        (await client.GetAsync($"/api/accounting/cash-flow?year=2026&month=10&cash={codes}", Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_code_is_a_400_that_names_it()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var client = await _callers.SuperAdminAsync(null);

        var response = await client.GetAsync($"/api/accounting/cash-flow?year=2026&month=10&cash={cash},nope-9", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("nope-9");
    }

    [Fact]
    public async Task No_token_is_a_401()
    {
        using var anonymous = fixture.CreateClient();

        (await anonymous.GetAsync("/api/accounting/cash-flow?year=2026&month=10&cash=1010", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_view_ledger_gets_a_403()
    {
        var role = await _callers.RoleAsync("manage_config");
        var client = await _callers.ClientAsync(null, [role]);

        (await client.GetAsync("/api/accounting/cash-flow?year=2026&month=10&cash=1010", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
