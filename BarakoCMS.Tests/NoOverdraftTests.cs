using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>An account marked NoOverdraft cannot be posted below zero, from any caller of the ledger.</summary>
[Collection("Sequential")]
public class NoOverdraftTests
{
    private readonly IntegrationTestFixture _factory;

    public NoOverdraftTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly DateOnly Day = new(2026, 1, 15);

    private sealed record Chart(string Cash, string Child, string Equity, string Plain);

    private async Task<Chart> SeedAsync(bool flagCash)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var chart = new Chart($"1000-{tag}", $"1000-{tag}-c", $"3000-{tag}", $"1100-{tag}");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var accounts = new AccountService(session);
        await accounts.UpsertManyAsync(new[]
        {
            new Account { Code = chart.Cash, Name = "Cash", Type = AccountType.Asset, NoOverdraft = flagCash },
            new Account { Code = chart.Child, Name = "Cash child", Type = AccountType.Asset, ParentCode = chart.Cash },
            new Account { Code = chart.Equity, Name = "Equity", Type = AccountType.Equity },
            new Account { Code = chart.Plain, Name = "Receivable", Type = AccountType.Asset },
        }, TestContext.Current.CancellationToken);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return chart;
    }

    private async Task<PostResult> PostAsync(string debit, string credit, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
        return await ledger.PostAsync(
            new PostEntryCommand(Day, "test", new[]
            {
                new PostLine(debit, amount, 0),
                new PostLine(credit, 0, amount),
            }), Guid.NewGuid(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_entry_that_would_overdraw_a_flagged_account_is_refused()
    {
        var c = await SeedAsync(flagCash: true);
        (await PostAsync(c.Cash, c.Equity, 100m)).Ok.Should().BeTrue();

        var result = await PostAsync(c.Equity, c.Cash, 100.01m);

        result.Ok.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(c.Cash).And.Contain("below zero");
    }

    [Fact]
    public async Task An_entry_that_leaves_a_flagged_account_at_exactly_zero_passes()
    {
        var c = await SeedAsync(flagCash: true);
        (await PostAsync(c.Cash, c.Equity, 100m)).Ok.Should().BeTrue();

        (await PostAsync(c.Equity, c.Cash, 100m)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task A_child_of_a_flagged_account_is_covered_too()
    {
        var c = await SeedAsync(flagCash: true);

        var result = await PostAsync(c.Equity, c.Child, 5m);

        result.Ok.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.Should().Contain(c.Child);
    }

    [Fact]
    public async Task An_unflagged_account_can_still_go_negative()
    {
        var c = await SeedAsync(flagCash: true);

        (await PostAsync(c.Equity, c.Plain, 50m)).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Two_concurrent_posts_that_each_fit_alone_cannot_both_succeed()
    {
        var c = await SeedAsync(flagCash: true);
        (await PostAsync(c.Cash, c.Equity, 100m)).Ok.Should().BeTrue();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => PostAsync(c.Equity, c.Cash, 60m))));

        results.Count(r => r.Ok).Should().Be(1);
    }

    [Fact]
    public async Task The_generic_route_answers_400_not_500()
    {
        var c = await SeedAsync(flagCash: true);
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/accounting/journal-entries", new
        {
            date = "2026-01-15",
            memo = "overdraw",
            lines = new object[]
            {
                new { accountCode = c.Equity, debit = 10m, credit = 0m },
                new { accountCode = c.Cash, debit = 0m, credit = 10m },
            },
        });

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("below zero");
    }
}
