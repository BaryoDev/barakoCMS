using System.Net;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using BarakoCMS.Tests.Features.Config;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>The accountingMappings section: stored, exported, checked against the chart and gated.</summary>
[Collection("Sequential")]
public class AccountMappingTests(IntegrationTestFixture fixture)
{
    private readonly ConfigCallers _callers = new(fixture);

    private static string Name() => $"map-{Guid.NewGuid():N}"[..14];

    private static object Rule(string cash, string income, string side = "debit") => new
    {
        lines = new[]
        {
            new { account = cash, side, amount = "{{Data.Total}}" },
            new { account = income, side = "credit", amount = "{{Data.Total}}" },
        },
        skipIfZero = true,
    };

    private static object Document(string name, object rule) =>
        new { version = 1, accountingMappings = new Dictionary<string, object> { [name] = rule } };

    [Fact]
    public async Task A_mapping_applies_and_exports_the_same()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = Name();
        var client = await _callers.SuperAdminAsync(null);

        var (applied, body) = await ApplyAsync(client, Document(name, Rule(cash, income)), allowDestructive: true);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var (_, export) = await ExportAsync(client, "accountingMappings");
        var rule = export.GetProperty("accountingMappings").GetProperty(name);
        rule.GetProperty("skipIfZero").GetBoolean().Should().BeTrue();
        var lines = rule.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines[0].GetProperty("account").GetString().Should().Be(cash);
        lines[0].GetProperty("side").GetString().Should().Be("debit");
        lines[1].GetProperty("side").GetString().Should().Be("credit");
        lines[1].GetProperty("amount").GetString().Should().Be("{{Data.Total}}");
    }

    [Fact]
    public async Task A_mapping_is_saved_with_the_accounts_spelled_as_the_chart_spells_them()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "CASH", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "SALES", AccountType.Income);
        var name = Name();
        var client = await _callers.SuperAdminAsync(null);

        var (applied, body) = await ApplyAsync(client, Document(name, Rule(cash.ToUpperInvariant(), income.ToUpperInvariant())), allowDestructive: true);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());

        var (_, export) = await ExportAsync(client, "accountingMappings");
        var lines = export.GetProperty("accountingMappings").GetProperty(name).GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => l.GetProperty("account").GetString()).Should().Equal(cash, income);
    }

    [Fact]
    public async Task A_side_other_than_debit_or_credit_is_a_400()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var client = await _callers.SuperAdminAsync(null);

        var (response, body) = await ApplyAsync(client, Document(Name(), Rule(cash, income, side: "left")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("side must be debit or credit");
    }

    [Fact]
    public async Task An_account_that_is_not_in_the_chart_is_a_400_that_names_the_code()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var missing = AccountingTestData.Code("NOPE");
        var client = await _callers.SuperAdminAsync(null);

        var (response, body) = await ApplyAsync(client, Document(Name(), Rule(cash, missing)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain(missing);
    }

    [Fact]
    public async Task A_caller_without_manage_accounting_mappings_gets_a_403()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var role = await _callers.RoleAsync(SystemCapabilities.ManageConfig);
        var client = await _callers.ClientAsync(null, [role]);

        var (response, body) = await ApplyAsync(client, Document(Name(), Rule(cash, income)));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
    }

    [Fact]
    public async Task The_store_returns_the_rule_by_name_and_null_for_an_unknown_one()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = Name();
        var client = await _callers.SuperAdminAsync(null);
        (await ApplyAsync(client, Document(name, Rule(cash, income)), allowDestructive: true)).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<AccountMappingStore>();

        var rule = await store.GetAsync(name, TestContext.Current.CancellationToken);
        rule.Should().NotBeNull();
        rule!.SkipIfZero.Should().BeTrue();
        rule.Lines.Select(l => (l.Account, l.Side, l.Amount)).Should().Equal(
            (cash, "debit", "{{Data.Total}}"), (income, "credit", "{{Data.Total}}"));
        (await store.GetAsync(Name(), TestContext.Current.CancellationToken)).Should().BeNull();
    }
}
