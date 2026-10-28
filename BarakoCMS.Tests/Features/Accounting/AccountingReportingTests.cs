using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.Accounting;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// Reporting now reads the ledger out of content types rather than typed documents. These tests post
/// through the generic content endpoint and then assert the computed numbers, because the risk of
/// this conversion was never "does it compile" — it was whether the balances still come out right.
/// </summary>
[Collection("Integration1")]
public class AccountingReportingTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private bool _seeded;

    public AccountingReportingTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync()
    {
        if (_seeded) return;

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        foreach (var def in new[]
                 {
                     AccountingContentTypes.AccountDefinition(),
                     AccountingContentTypes.JournalEntryDefinition(),
                 })
        {
            if (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(t => t.Name == def.Name) is null)
                session.Store(def);
        }

        if (await session.LoadAsync<Role>(barakoCMS.Data.DataSeeder.SuperAdminRoleId) is null)
            session.Store(new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin" });

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"rep_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = new List<Guid> { barakoCMS.Data.DataSeeder.SuperAdminRoleId },
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
        _seeded = true;
    }

    private async Task PostAsync(string contentType, object data)
    {
        var res = await _client.PostAsJsonAsync("/api/contents",
            new { contentType, status = 1, sensitivity = 0, data });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<string> AccountAsync(string prefix, string type)
    {
        await SeedAsync();
        var code = $"{prefix}-{Guid.NewGuid():N}";
        await PostAsync(AccountingContentTypes.Account,
            new { Code = code, Name = $"Account {code}", Type = type, IsActive = true });
        return code;
    }

    private ReportingService Reporting(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ReportingService>();

    [Fact]
    public async Task A_trial_balance_computed_from_content_balances_and_uses_each_accounts_normal_side()
    {
        var cash = await AccountAsync("1000", "Asset");     // debit-normal
        var income = await AccountAsync("4000", "Income");  // credit-normal

        await PostAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Dues",
            Lines = new[]
            {
                new { AccountCode = cash, Debit = 500.25m, Credit = 0m },
                new { AccountCode = income, Debit = 0m, Credit = 500.25m },
            },
        });

        using var scope = _factory.Services.CreateScope();
        var balances = await Reporting(scope).BalancesAsync(null, default);

        balances.Single(b => b.Code == cash).Balance.Should().Be(500.25m,
            "an asset is debit-normal, so a debit increases it");
        balances.Single(b => b.Code == income).Balance.Should().Be(500.25m,
            "income is credit-normal, so a credit increases it");

        // The defining property of a trial balance.
        var debitNormalTotal = balances.Where(b => b.Code == cash).Sum(b => b.Balance);
        var creditNormalTotal = balances.Where(b => b.Code == income).Sum(b => b.Balance);
        debitNormalTotal.Should().Be(creditNormalTotal, "the books must balance");
    }

    [Fact]
    public async Task Balances_stay_exact_across_many_fractional_postings()
    {
        var cash = await AccountAsync("1000", "Asset");
        var income = await AccountAsync("4000", "Income");

        // Ten 0.10 postings. Under the old double-backed storage this accumulated drift; as decimals
        // it must be exactly 1.00.
        for (var i = 0; i < 10; i++)
        {
            await PostAsync(AccountingContentTypes.JournalEntry, new
            {
                Date = "2026-08-03",
                Memo = $"Posting {i}",
                Lines = new[]
                {
                    new { AccountCode = cash, Debit = 0.10m, Credit = 0m },
                    new { AccountCode = income, Debit = 0m, Credit = 0.10m },
                },
            });
        }

        using var scope = _factory.Services.CreateScope();
        var balances = await Reporting(scope).BalancesAsync(null, default);

        balances.Single(b => b.Code == cash).Balance.Should().Be(1.00m,
            "ten 0.10 postings must total exactly 1.00, not 0.9999999999999999");
    }

    [Fact]
    public async Task An_account_ledger_carries_a_running_balance()
    {
        var cash = await AccountAsync("1000", "Asset");
        var income = await AccountAsync("4000", "Income");

        foreach (var amount in new[] { 100m, 250m })
        {
            await PostAsync(AccountingContentTypes.JournalEntry, new
            {
                Date = "2026-08-03",
                Memo = $"Receipt {amount}",
                Lines = new[]
                {
                    new { AccountCode = cash, Debit = amount, Credit = 0m },
                    new { AccountCode = income, Debit = 0m, Credit = amount },
                },
            });
        }

        using var scope = _factory.Services.CreateScope();
        var ledger = await Reporting(scope).AccountLedgerAsync(cash, default);

        ledger.Should().NotBeNull();
        ledger!.Lines.Should().HaveCount(2);
        ledger.Lines.Last().RunningBalance.Should().Be(350m);
        ledger.Balance.Should().Be(350m);
    }

    [Fact]
    public async Task A_voided_status_is_a_marker_and_the_entry_stays_in_balances()
    {
        var cash = await AccountAsync("1000", "Asset");
        var income = await AccountAsync("4000", "Income");

        await PostAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Voided posting",
            Status = "Voided",
            Lines = new[]
            {
                new { AccountCode = cash, Debit = 999m, Credit = 0m },
                new { AccountCode = income, Debit = 0m, Credit = 999m },
            },
        });

        using var scope = _factory.Services.CreateScope();
        var balances = await Reporting(scope).BalancesAsync(null, default);

        balances.Single(b => b.Code == cash).Balance.Should().Be(999m,
            "voiding marks an entry, it does not take it out of the books");
    }

    private Task PostEntryAsync(string date, params (string Code, decimal Debit, decimal Credit)[] lines) =>
        PostAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = date,
            Memo = "Cash flow",
            Lines = lines.Select(l => new { AccountCode = l.Code, l.Debit, l.Credit }).ToArray(),
        });

    [Fact]
    public async Task Cash_flow_ignores_a_transfer_between_cash_accounts()
    {
        var bank = await AccountAsync("1000", "Asset");
        var petty = await AccountAsync("1010", "Asset");
        var income = await AccountAsync("4100", "Income");

        await PostEntryAsync("2026-08-03", (bank, 1000.50m, 0m), (income, 0m, 1000.50m));
        await PostEntryAsync("2026-08-05", (petty, 400.25m, 0m), (bank, 0m, 400.25m));

        using var scope = _factory.Services.CreateScope();
        var flow = await Reporting(scope).CashFlowAsync(new[] { bank, petty }, 2026, 8, default);

        flow.Receipts.Should().Be(1000.50m);
        flow.Disbursements.Should().Be(0m);
        flow.Closing.Should().Be(1000.50m);
        flow.Lines.Should().HaveCount(3);
        // The running balance moves leg by leg, as CashFlowTests pins, and is back where it started after the transfer.
        flow.Lines.Select(l => l.RunningBalance).Should().Equal(1000.50m, 1400.75m, 1000.50m);
    }

    [Fact]
    public async Task Cash_flow_counts_only_the_net_of_a_mixed_entry()
    {
        var bank = await AccountAsync("1000", "Asset");
        var petty = await AccountAsync("1010", "Asset");
        var income = await AccountAsync("4100", "Income");

        await PostEntryAsync("2026-08-03", (petty, 700.00m, 0m), (bank, 0m, 500.00m), (income, 0m, 200.00m));

        using var scope = _factory.Services.CreateScope();
        var flow = await Reporting(scope).CashFlowAsync(new[] { bank, petty }, 2026, 8, default);

        flow.Receipts.Should().Be(200m);
        flow.Disbursements.Should().Be(0m);
    }

    [Fact]
    public async Task Cash_flow_opening_includes_entries_before_the_month()
    {
        var bank = await AccountAsync("1000", "Asset");
        var income = await AccountAsync("4100", "Income");

        await PostEntryAsync("2026-07-20", (bank, 0.01m, 0m), (income, 0m, 0.01m));

        using var scope = _factory.Services.CreateScope();
        var flow = await Reporting(scope).CashFlowAsync(new[] { bank }, 2026, 8, default);

        flow.Opening.Should().Be(0.01m);
    }

    [Fact]
    public async Task Cash_flow_counts_a_payment_out_as_a_disbursement()
    {
        var bank = await AccountAsync("1000", "Asset");
        var expense = await AccountAsync("5100", "Expense");

        await PostEntryAsync("2026-08-03", (expense, 100.00m, 0m), (bank, 0m, 100.00m));

        using var scope = _factory.Services.CreateScope();
        var flow = await Reporting(scope).CashFlowAsync(new[] { bank }, 2026, 8, default);

        flow.Disbursements.Should().Be(100m);
        flow.Receipts.Should().Be(0m);
    }
}
