using Xunit;
using FluentAssertions;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// The chart-of-accounts API that hosts actually use.
///
/// It had no tests at all, which is the wrong way round: nothing inside barakoCMS calls it, so it
/// looked like dead code, but BaryoClub uses it in seven places — seeding the chart, creating member
/// accounts, batch charging, delisting, and reminders. Being consumer-only means a break here shows
/// up in someone else's repository, after a release, rather than in this one's CI.
/// </summary>
[Collection("Sequential")]
public class AccountServiceTests
{
    private readonly IntegrationTestFixture _factory;

    public AccountServiceTests(IntegrationTestFixture factory) => _factory = factory;

    private IDocumentStore Store()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDocumentStore>();
    }

    private static Account Acct(string code, string name = "Account", AccountType type = AccountType.Asset) =>
        new() { Code = code, Name = name, Type = type, IsActive = true };

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task An_upserted_account_can_be_read_back()
    {
        var tag = Tag();
        using var s = Store().LightweightSession();
        var svc = new AccountService(s);

        await svc.UpsertAsync(Acct($"1000-{tag}", "Cash on hand"));
        await s.SaveChangesAsync();

        var found = await new AccountService(s).GetByCodeAsync($"1000-{tag}");
        found.Should().NotBeNull();
        found!.Name.Should().Be("Cash on hand");
        found.Type.Should().Be(AccountType.Asset);
        found.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Lookup_by_code_ignores_case()
    {
        var tag = Tag();
        using var s = Store().LightweightSession();
        await new AccountService(s).UpsertAsync(Acct($"ab-{tag}"));
        await s.SaveChangesAsync();

        // Hosts pass codes through URLs and spreadsheets; a case-sensitive miss would read as
        // "account not found" and send a charge to the wrong place, or nowhere.
        (await new AccountService(s).GetByCodeAsync($"AB-{tag}")).Should().NotBeNull();
    }

    [Fact]
    public async Task Upserting_an_existing_code_updates_in_place_rather_than_duplicating()
    {
        var tag = Tag();
        var code = $"4000-{tag}";

        using (var s1 = Store().LightweightSession())
        {
            await new AccountService(s1).UpsertAsync(Acct(code, "Dues", AccountType.Income));
            await s1.SaveChangesAsync();
        }

        using (var s2 = Store().LightweightSession())
        {
            await new AccountService(s2).UpsertAsync(Acct(code, "Membership dues", AccountType.Income));
            await s2.SaveChangesAsync();
        }

        // Two accounts sharing a code means every balance for that code is split across two
        // documents, and which one a lookup returns is arbitrary.
        using var q = Store().QuerySession();
        var all = await new AccountService(q).GetAllAsync();
        all.Where(a => a.Code == code).Should().HaveCount(1);
        all.Single(a => a.Code == code).Name.Should().Be("Membership dues");
    }

    [Fact]
    public async Task The_chart_comes_back_ordered_by_code()
    {
        var tag = Tag();
        using var s = Store().LightweightSession();
        await new AccountService(s).UpsertManyAsync(new[]
        {
            Acct($"5000-{tag}"), Acct($"1000-{tag}"), Acct($"3000-{tag}"),
        });
        await s.SaveChangesAsync();

        var mine = (await new AccountService(s).GetAllAsync())
            .Where(a => a.Code.EndsWith(tag)).Select(a => a.Code).ToList();

        mine.Should().Equal($"1000-{tag}", $"3000-{tag}", $"5000-{tag}");
    }

    [Fact]
    public async Task A_read_only_service_refuses_to_write_instead_of_silently_dropping_it()
    {
        using var q = Store().QuerySession();
        var svc = new AccountService(q);

        // Silently doing nothing here would be the worst outcome: a seeding run that reports success
        // and leaves the chart empty.
        var act = async () => await svc.UpsertAsync(Acct("1000-readonly"));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Member_metadata_survives_the_round_trip()
    {
        var tag = Tag();
        var memberId = Guid.NewGuid();

        using var s = Store().LightweightSession();
        await new AccountService(s).UpsertAsync(new Account
        {
            Code = $"1200-{tag}", Name = "Receivable — J. Cruz", Type = AccountType.Asset,
            MemberId = memberId, PayeeName = "J. Cruz", ParentCode = $"1000-{tag}", IsActive = true,
        });
        await s.SaveChangesAsync();

        // BaryoClub keys a member's statement off MemberId. Losing it detaches the member from their
        // own receivable account, which is the cross-member read the project treats as a security bug.
        var found = (await new AccountService(s).GetByCodeAsync($"1200-{tag}"))!;
        found.MemberId.Should().Be(memberId);
        found.PayeeName.Should().Be("J. Cruz");
        found.ParentCode.Should().Be($"1000-{tag}");
    }

    [Fact]
    public async Task Deactivating_an_account_is_persisted()
    {
        var tag = Tag();
        var code = $"1300-{tag}";

        using var s = Store().LightweightSession();
        await new AccountService(s).UpsertAsync(Acct(code));
        await s.SaveChangesAsync();

        var acct = (await new AccountService(s).GetByCodeAsync(code))!;
        acct.IsActive = false;
        await new AccountService(s).UpsertAsync(acct);
        await s.SaveChangesAsync();

        // The journal hook refuses postings to an inactive account, so an ignored deactivation means
        // a delisted member keeps accruing charges.
        (await new AccountService(s).GetByCodeAsync(code))!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Count_matches_what_the_chart_returns()
    {
        using var s = Store().LightweightSession();
        var svc = new AccountService(s);
        (await svc.CountAsync()).Should().Be((await svc.GetAllAsync()).Count);
    }

    /// <summary>
    /// Repeating a code inside one uncommitted unit of work.
    ///
    /// <see cref="AccountService.UpsertAsync"/> looks for an existing account with
    /// <c>session.Query</c>, which reads the database — so accounts stored earlier in the same
    /// uncommitted batch are invisible to it. <see cref="AccountService.UpsertManyAsync"/> is a loop
    /// over that method and is what a host uses to seed a whole chart in one transaction, which is
    /// exactly where a repeated code is most likely to appear.
    /// </summary>
    [Fact]
    public async Task Repeating_a_code_within_one_unit_of_work_does_not_create_two_accounts()
    {
        var tag = Tag();
        var code = $"6000-{tag}";

        using var s = Store().LightweightSession();
        await new AccountService(s).UpsertManyAsync(new[]
        {
            Acct(code, "First spelling"),
            Acct(code, "Second spelling"),
        });
        await s.SaveChangesAsync();

        using var q = Store().QuerySession();
        var matching = (await new AccountService(q).GetAllAsync()).Where(a => a.Code == code).ToList();
        matching.Should().HaveCount(1, "one code is one account, whichever transaction it arrived in");
    }
}
