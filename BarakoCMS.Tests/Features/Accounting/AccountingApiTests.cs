using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// The module's own HTTP surface: POST /api/accounting/journal-entries and the accounts endpoints.
///
/// These had no tests at all, while carrying real money: BaryoClub posts a treasurer's entries through
/// the journal-entries route. Accounting moved to content types, and the content path is well covered,
/// but this route is still live, still registered, and still what an existing consumer calls — so a
/// change that satisfied the content tests could break the thing actually in use.
///
/// The invariants worth stating: an unbalanced entry never reaches the store, a rejected post consumes
/// no entry number, amounts survive as decimal, and posting requires an accounting role.
/// </summary>
[Collection("Sequential")]
public class AccountingApiTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private Guid _userId;
    private bool _seeded;

    public AccountingApiTests(IntegrationTestFixture factory)
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
            var existing = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(t => t.Name == def.Name);
            if (existing is null) session.Store(def);
        }

        if (await session.LoadAsync<Role>(barakoCMS.Data.DataSeeder.SuperAdminRoleId) is null)
        {
            session.Store(new Role
            {
                Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId,
                Name = "SuperAdmin",
                Description = "Full system access",
            });
        }

        // The endpoint loads the caller by their UserId claim, so the user has to exist, not just the token.
        _userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = _userId,
            Username = $"ledger_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = new List<Guid> { barakoCMS.Data.DataSeeder.SuperAdminRoleId },
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, _userId.ToString()));
        _seeded = true;
    }

    private async Task<string> AccountAsync(string code, string type = "Asset")
    {
        await SeedAsync();
        var res = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = AccountingContentTypes.Account,
            status = 1,
            sensitivity = 0,
            data = new { Code = code, Name = $"Account {code}", Type = type, IsActive = true },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return code;
    }

    private static object Line(string code, decimal debit, decimal credit) =>
        new { AccountCode = code, Debit = debit, Credit = credit };

    private Task<HttpResponseMessage> PostEntryAsync(object body) =>
        _client.PostAsJsonAsync("/api/accounting/journal-entries", body);

    private static object Entry(string memo, params object[] lines) =>
        new { Date = "2026-03-01", Memo = memo, Lines = lines };

    private async Task<int> EntryCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var all = await s.Query<Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .ToListAsync();
        return all.Count;
    }

    [Fact]
    public async Task A_balanced_entry_posts_and_is_numbered()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var income = await AccountAsync($"4000-{suffix}", "Income");

        var res = await PostEntryAsync(Entry("membership dues", Line(cash, 1500.50m, 0m), Line(income, 0m, 1500.50m)));

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("entryNumber").GetString().Should().NotBeNullOrWhiteSpace(
            "an entry without a number cannot be referred to in a statement");
        body.GetProperty("amount").GetDecimal().Should().Be(1500.50m);
    }

    [Fact]
    public async Task An_unbalanced_entry_is_refused_and_stores_nothing()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var income = await AccountAsync($"4000-{suffix}", "Income");
        var before = await EntryCountAsync();

        // One peso out. The whole point of double entry is that this cannot be stored.
        var res = await PostEntryAsync(Entry("off by one", Line(cash, 100m, 0m), Line(income, 0m, 99m)));

        res.IsSuccessStatusCode.Should().BeFalse(await res.Content.ReadAsStringAsync());
        (await EntryCountAsync()).Should().Be(before, "a rejected entry must not reach the ledger");
    }

    [Fact]
    public async Task A_rejected_entry_does_not_consume_an_entry_number()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var income = await AccountAsync($"4000-{suffix}", "Income");

        var first = await PostEntryAsync(Entry("first", Line(cash, 10m, 0m), Line(income, 0m, 10m)));
        var firstNumber = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entryNumber").GetString();

        // A failed post in between must not burn a number, or the ledger shows a gap and a treasurer
        // has to explain a missing entry that never existed.
        (await PostEntryAsync(Entry("rejected", Line(cash, 10m, 0m), Line(income, 0m, 9m))))
            .IsSuccessStatusCode.Should().BeFalse();

        var third = await PostEntryAsync(Entry("second", Line(cash, 20m, 0m), Line(income, 0m, 20m)));
        var thirdNumber = (await third.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entryNumber").GetString();

        var firstSeq = int.Parse(new string(firstNumber!.Where(char.IsDigit).ToArray())[^4..]);
        var thirdSeq = int.Parse(new string(thirdNumber!.Where(char.IsDigit).ToArray())[^4..]);
        (thirdSeq - firstSeq).Should().Be(1, "the sequence should advance once, not twice");
    }

    [Fact]
    public async Task An_entry_against_an_unknown_account_is_refused()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var before = await EntryCountAsync();

        var res = await PostEntryAsync(Entry("typo", Line(cash, 50m, 0m), Line("no-such-account", 0m, 50m)));

        res.IsSuccessStatusCode.Should().BeFalse(await res.Content.ReadAsStringAsync());
        (await EntryCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task An_entry_with_too_few_lines_is_refused()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");

        // One line is refused, though the balance rule would have caught it anyway.
        (await PostEntryAsync(Entry("one leg", Line(cash, 100m, 0m))))
            .IsSuccessStatusCode.Should().BeFalse();

        // No lines is the case where the minimum is the only thing standing in the way: debits and
        // credits are both zero, so the entry balances, and the "total must exceed zero" rule is
        // itself conditioned on there being lines. Drop the minimum and an empty entry posts.
        (await PostEntryAsync(Entry("nothing at all")))
            .IsSuccessStatusCode.Should().BeFalse("an entry with no lines is not an entry");
    }

    [Fact]
    public async Task Fractional_amounts_survive_the_round_trip_exactly()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var income = await AccountAsync($"4000-{suffix}", "Income");

        // 0.1 + 0.2 is 0.30000000000000004 as double. If any part of this path is double, the entry
        // either fails to balance or stores a number a treasurer cannot reconcile.
        var res = await PostEntryAsync(Entry("thirds",
            Line(cash, 0.1m, 0m), Line(cash, 0.2m, 0m), Line(income, 0m, 0.3m)));

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("amount").GetDecimal()
            .Should().Be(0.3m);
    }

    [Fact]
    public async Task A_large_amount_keeps_its_centavos()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var cash = await AccountAsync($"1000-{suffix}");
        var income = await AccountAsync($"4000-{suffix}", "Income");

        // Far beyond float's exact-integer range, with centavos that a double would round away.
        const decimal big = 12_345_678.91m;
        var res = await PostEntryAsync(Entry("annual", Line(cash, big, 0m), Line(income, 0m, big)));

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("amount").GetDecimal()
            .Should().Be(big);
    }

    [Fact]
    public async Task Posting_requires_an_accounting_role()
    {
        await SeedAsync();
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/accounting/journal-entries", Entry("anon", Line("1000", 1m, 0m))))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var editor = _factory.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "Editor" }, Guid.NewGuid().ToString()));
        (await editor.PostAsJsonAsync("/api/accounting/journal-entries", Entry("editor", Line("1000", 1m, 0m))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "an editor has no business posting to the ledger");
    }

    [Fact]
    public async Task Accounts_can_be_created_and_listed_through_the_module_endpoints()
    {
        await SeedAsync();
        var code = $"5000-{Guid.NewGuid().ToString("N")[..6]}";

        // Type is the AccountType enum and no string-enum converter is registered, so this endpoint
        // takes the ordinal. The content-type path takes "Expense" as a string for the same concept —
        // worth knowing before writing a client against either.
        var created = await _client.PostAsJsonAsync("/api/accounting/accounts", new
        {
            Code = code,
            Name = "Office supplies",
            Type = (int)AccountType.Expense,
        });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync());

        var listed = await _client.GetAsync("/api/accounting/accounts");
        listed.IsSuccessStatusCode.Should().BeTrue();
        (await listed.Content.ReadAsStringAsync()).Should().Contain(code);
    }

    [Fact]
    public async Task Listing_accounts_requires_a_role()
    {
        await SeedAsync();
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/accounting/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
