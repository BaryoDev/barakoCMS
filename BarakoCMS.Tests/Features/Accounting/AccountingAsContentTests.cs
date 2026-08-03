using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.Accounting;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// Accounting is modelled as ordinary content types (the project's content-type-first rule), with the
/// rules a schema can't express living in content lifecycle hooks. These tests go through the
/// <em>generic</em> <c>/api/contents</c> endpoint on purpose: that is the claim being verified — that
/// accounting needs no bespoke write API, and that routing it through the generic one does not lose
/// the balance invariant.
/// </summary>
[Collection("Sequential")]
public class AccountingAsContentTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private bool _seeded;

    public AccountingAsContentTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Seeds the accounting content-type definitions plus a real SuperAdmin user, and authenticates
    /// the client as that user. The endpoints load the caller by their UserId claim, so a token
    /// alone isn't enough — the user has to exist.
    /// </summary>
    private async Task EnsureContentTypesAsync()
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
            var existing = await session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(t => t.Name == def.Name);
            if (existing is null) session.Store(def);
        }

        var superAdminRole = await session.LoadAsync<Role>(barakoCMS.Data.DataSeeder.SuperAdminRoleId);
        if (superAdminRole is null)
        {
            session.Store(new Role
            {
                Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId,
                Name = "SuperAdmin",
                Description = "Full system access",
            });
        }

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"acct_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = new List<Guid> { barakoCMS.Data.DataSeeder.SuperAdminRoleId },
        });

        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
        _seeded = true;
    }

    private async Task<HttpResponseMessage> PostContentAsync(string contentType, object data) =>
        await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType,
            status = 1, // Published
            sensitivity = 0,
            data,
        });

    private async Task<string> CreateAccountAsync(string code, string type = "Asset")
    {
        await EnsureContentTypesAsync();
        var res = await PostContentAsync(AccountingContentTypes.Account, new
        {
            Code = code,
            Name = $"Account {code}",
            Type = type,
            IsActive = true,
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return code;
    }

    private static object Line(string code, decimal debit, decimal credit) =>
        new { AccountCode = code, Debit = debit, Credit = credit };

    // ---- chart of accounts -------------------------------------------------

    [Fact]
    public async Task An_account_can_be_created_through_the_generic_content_endpoint()
    {
        await EnsureContentTypesAsync();
        var res = await PostContentAsync(AccountingContentTypes.Account, new
        {
            Code = $"1000-{Guid.NewGuid():N}",
            Name = "Cash on hand",
            Type = "Asset",
            IsActive = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_account_with_an_unknown_class_is_rejected()
    {
        await EnsureContentTypesAsync();
        var res = await PostContentAsync(AccountingContentTypes.Account, new
        {
            Code = $"9999-{Guid.NewGuid():N}",
            Name = "Nonsense",
            Type = "Vibes",
            IsActive = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Asset");
    }

    [Fact]
    public async Task A_duplicate_account_code_is_rejected()
    {
        var code = $"1000-{Guid.NewGuid():N}";
        await CreateAccountAsync(code);

        var res = await PostContentAsync(AccountingContentTypes.Account, new
        {
            Code = code,
            Name = "Duplicate",
            Type = "Asset",
            IsActive = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("already in use");
    }

    [Fact]
    public async Task A_parent_code_that_does_not_resolve_is_rejected()
    {
        await EnsureContentTypesAsync();
        var res = await PostContentAsync(AccountingContentTypes.Account, new
        {
            Code = $"1100-{Guid.NewGuid():N}",
            Name = "Orphan",
            Type = "Asset",
            ParentCode = $"missing-{Guid.NewGuid():N}",
            IsActive = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("does not exist");
    }

    // ---- the balance invariant --------------------------------------------

    [Fact]
    public async Task A_balanced_entry_is_accepted_and_gets_a_server_stamped_number_and_amount()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Membership dues",
            Lines = new[] { Line(cash, 1500.50m, 0m), Line(income, 0m, 1500.50m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var read = store.QuerySession();
        var stored = (await read.LoadAsync<Content>(id))!;

        stored.Data["EntryNumber"].ToString().Should().StartWith("JE-2026-",
            "the server allocates the number; the caller never supplies it");
        stored.Data["Amount"].Should().BeOfType<decimal>().And.Be(1500.50m,
            "the total must stay an exact decimal, not a double");
        stored.Data["Status"].Should().Be("Posted");
    }

    [Fact]
    public async Task An_unbalanced_entry_is_rejected()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Off by a peso",
            Lines = new[] { Line(cash, 100m, 0m), Line(income, 0m, 99m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("not balanced",
            "this is the invariant that justifies accounting being a content type at all");
    }

    [Fact]
    public async Task An_entry_referencing_an_unknown_account_is_rejected()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");

        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Typo in the account code",
            Lines = new[] { Line(cash, 50m, 0m), Line("no-such-account", 0m, 50m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("does not exist");
    }

    [Fact]
    public async Task A_single_line_entry_is_rejected()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");

        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Half a posting",
            Lines = new[] { Line(cash, 100m, 0m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("at least two lines");
    }

    [Fact]
    public async Task A_line_with_both_a_debit_and_a_credit_is_rejected()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Both sides",
            Lines = new[] { Line(cash, 100m, 100m), Line(income, 0m, 100m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("cannot have both");
    }

    [Fact]
    public async Task Entry_numbers_are_sequential_and_never_reused()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        var numbers = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
            {
                Date = "2026-08-03",
                Memo = $"Posting {i}",
                Lines = new[] { Line(cash, 10m, 0m), Line(income, 0m, 10m) },
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

            var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("id").GetGuid();

            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var read = store.QuerySession();
            numbers.Add((await read.LoadAsync<Content>(id))!.Data["EntryNumber"].ToString()!);
        }

        numbers.Should().OnlyHaveUniqueItems("a sequence number must never be duplicated");
    }

    [Fact]
    public async Task A_rejected_entry_does_not_consume_a_sequence_number()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        async Task<string> PostGoodAsync()
        {
            var ok = await PostContentAsync(AccountingContentTypes.JournalEntry, new
            {
                Date = "2026-08-03",
                Memo = "Good",
                Lines = new[] { Line(cash, 10m, 0m), Line(income, 0m, 10m) },
            });
            ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
            var id = JsonDocument.Parse(await ok.Content.ReadAsStringAsync())
                .RootElement.GetProperty("id").GetGuid();

            using var scope = _factory.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var read = store.QuerySession();
            return (await read.LoadAsync<Content>(id))!.Data["EntryNumber"].ToString()!;
        }

        var before = await PostGoodAsync();

        // A rejected post must not burn a number — an auditor reading a gap in the sequence has to
        // be able to conclude an entry was deleted, not that a validation error ate one.
        var bad = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Unbalanced",
            Lines = new[] { Line(cash, 10m, 0m), Line(income, 0m, 9m) },
        });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var after = await PostGoodAsync();

        int Seq(string n) => int.Parse(n.Split('-').Last());
        (Seq(after) - Seq(before)).Should().Be(1, "the failed post must not have consumed a number");
    }

    [Fact]
    public async Task Money_survives_the_round_trip_as_exact_decimal()
    {
        var cash = await CreateAccountAsync($"1000-{Guid.NewGuid():N}");
        var income = await CreateAccountAsync($"4000-{Guid.NewGuid():N}", "Income");

        // 0.1 + 0.2 is the classic binary-floating-point trap; as decimals it is exactly 0.3.
        var res = await PostContentAsync(AccountingContentTypes.JournalEntry, new
        {
            Date = "2026-08-03",
            Memo = "Precision",
            Lines = new[] { Line(cash, 0.1m, 0m), Line(cash, 0.2m, 0m), Line(income, 0m, 0.3m) },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var id = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var read = store.QuerySession();
        var stored = (await read.LoadAsync<Content>(id))!;

        stored.Data["Amount"].Should().Be(0.3m,
            "if money were still a double this would balance to 0.30000000000000004 and be rejected");
    }
}
