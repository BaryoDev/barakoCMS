using Xunit;
using FluentAssertions;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// The one-shot move of an existing deployment's ledger from the old strongly-typed documents onto
/// content types.
///
/// This had no tests, and it is the single most dangerous piece of code in the module: it runs once,
/// against a real club's books, usually by someone following a runbook. A silent drop or a
/// double-post here is not a crash — it is a treasurer's balance being quietly wrong afterwards, with
/// the run already finished and the operator moving on.
///
/// Its doc comment makes two promises, so those are what gets pinned: it copies rather than moves,
/// and running it twice does not duplicate anything.
/// </summary>
[Collection("Sequential")]
public class AccountingMigrationTests
{
    private readonly IntegrationTestFixture _factory;

    public AccountingMigrationTests(IntegrationTestFixture factory) => _factory = factory;

    private IDocumentStore Store()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IDocumentStore>();
    }

    /// <summary>Old-shape data, tagged with a unique code prefix so parallel-safe assertions are possible.</summary>
    private static (Account cash, Account income, JournalEntry entry) Legacy(string tag)
    {
        var cash = new Account
        {
            Code = $"1000-{tag}", Name = "Cash on hand", Type = AccountType.Asset,
            IsActive = true, CreatedAt = new DateTime(2021, 5, 4, 9, 30, 0, DateTimeKind.Utc),
        };
        var income = new Account
        {
            Code = $"4000-{tag}", Name = "Dues", Type = AccountType.Income,
            IsActive = true, CreatedAt = new DateTime(2021, 5, 4, 9, 30, 0, DateTimeKind.Utc),
        };
        var entry = new JournalEntry
        {
            EntryNumber = $"JE-2021-{tag}",
            Date = new DateOnly(2021, 6, 1),
            Memo = "Q2 dues",
            Status = JournalStatus.Posted,
            Amount = 2500.25m,
            CreatedAt = new DateTime(2021, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            Lines = new List<JournalLine>
            {
                new() { AccountCode = cash.Code, Debit = 2500.25m, Credit = 0m },
                new() { AccountCode = income.Code, Debit = 0m, Credit = 2500.25m },
            },
        };
        return (cash, income, entry);
    }

    private async Task<string> SeedLegacyAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (cash, income, entry) = Legacy(tag);
        using var s = Store().LightweightSession();
        s.Store(cash, income);
        s.Store(entry);
        await s.SaveChangesAsync();
        return tag;
    }

    private async Task<List<Content>> ContentAsync(string type, string tag)
    {
        using var s = Store().QuerySession();
        var all = await s.Query<Content>().Where(c => c.ContentType == type).ToListAsync();
        return all.Where(c => System.Text.Json.JsonSerializer.Serialize(c.Data).Contains(tag)).ToList();
    }

    [Fact]
    public async Task It_copies_accounts_and_entries_onto_content_types()
    {
        var tag = await SeedLegacyAsync();

        using var s = Store().LightweightSession();
        var result = await AccountingMigration.RunAsync(s, Guid.NewGuid());

        result.AccountsCopied.Should().BeGreaterThanOrEqualTo(2);
        result.EntriesCopied.Should().BeGreaterThanOrEqualTo(1);

        var accounts = await ContentAsync(AccountingContentTypes.Account, tag);
        accounts.Should().HaveCount(2);

        var entries = await ContentAsync(AccountingContentTypes.JournalEntry, tag);
        entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task It_leaves_the_original_documents_in_place()
    {
        var tag = await SeedLegacyAsync();

        using var s = Store().LightweightSession();
        await AccountingMigration.RunAsync(s, Guid.NewGuid());

        // "Copy, not move" is the safety property the whole design rests on: if the converted shape
        // turns out wrong, the books are still on disk and the migration can be re-run. A version
        // that deleted as it went would pass every other test here.
        using var q = Store().QuerySession();
        (await q.Query<Account>().Where(a => a.Code == $"1000-{tag}").ToListAsync())
            .Should().HaveCount(1, "the original account must survive the migration");
        (await q.Query<JournalEntry>().Where(e => e.EntryNumber == $"JE-2021-{tag}").ToListAsync())
            .Should().HaveCount(1, "the original entry must survive the migration");
    }

    [Fact]
    public async Task Running_it_twice_copies_nothing_the_second_time()
    {
        var tag = await SeedLegacyAsync();

        using (var s1 = Store().LightweightSession())
            await AccountingMigration.RunAsync(s1, Guid.NewGuid());

        using var s2 = Store().LightweightSession();
        var second = await AccountingMigration.RunAsync(s2, Guid.NewGuid());

        // An operator who is unsure whether the first run finished will run it again. If that
        // double-posts the ledger, every balance afterwards is wrong and nothing announces it.
        second.EntriesCopied.Should().Be(0, "a second run must not re-post the ledger");
        second.AccountsCopied.Should().Be(0, "a second run must not duplicate the chart");
        second.EntriesSkipped.Should().BeGreaterThan(0, "the entries should be recognised, not invisible");

        (await ContentAsync(AccountingContentTypes.JournalEntry, tag)).Should().HaveCount(1);
        (await ContentAsync(AccountingContentTypes.Account, tag)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Amounts_and_line_values_survive_as_decimal()
    {
        var tag = await SeedLegacyAsync();

        using var s = Store().LightweightSession();
        await AccountingMigration.RunAsync(s, Guid.NewGuid());

        var entry = (await ContentAsync(AccountingContentTypes.JournalEntry, tag)).Single();

        // The centavos are the point. A migration that round-trips money through double moves a
        // real club's books by amounts too small for anyone to notice on the day.
        Dec(entry.Data, "Amount").Should().Be(2500.25m);

        var lines = ((System.Collections.IEnumerable)entry.Data["Lines"]).Cast<object>().Select(AsDict).ToList();
        var debits = lines.Select(l => Dec(l, "Debit")).ToList();
        var credits = lines.Select(l => Dec(l, "Credit")).ToList();

        debits.Sum().Should().Be(2500.25m);
        credits.Sum().Should().Be(2500.25m);
        debits.Sum().Should().Be(credits.Sum(), "a migrated entry that no longer balances is a corrupted ledger");
    }

    [Fact]
    public async Task The_original_dates_are_preserved()
    {
        var tag = await SeedLegacyAsync();

        using var s = Store().LightweightSession();
        await AccountingMigration.RunAsync(s, Guid.NewGuid());

        // Stamping migration day onto the records would file every historical entry under the date
        // the move happened, which quietly rewrites every period report.
        var entry = (await ContentAsync(AccountingContentTypes.JournalEntry, tag)).Single();
        Str(entry.Data, "Date").Should().Be("2021-06-01");
        entry.CreatedAt.Should().BeCloseTo(new DateTime(2021, 6, 1, 8, 0, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(1));

        var account = (await ContentAsync(AccountingContentTypes.Account, tag))
            .Single(a => Str(a.Data, "Code") == $"1000-{tag}");
        account.CreatedAt.Should().BeCloseTo(new DateTime(2021, 5, 4, 9, 30, 0, DateTimeKind.Utc), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Migrated_entries_are_published_and_not_marked_sensitive()
    {
        var tag = await SeedLegacyAsync();

        using var s = Store().LightweightSession();
        await AccountingMigration.RunAsync(s, Guid.NewGuid());

        // A migrated ledger that landed as Draft would be invisible to every report, which reads as
        // "the migration lost my data" even though it is all there.
        (await ContentAsync(AccountingContentTypes.JournalEntry, tag)).Single()
            .Status.Should().Be(ContentStatus.Published);
    }

    // ContentData is internal to the module, so the bag is read directly here rather than widening
    // production visibility to suit a test.
    private static decimal Dec(Dictionary<string, object> d, string key) =>
        d[key] is System.Text.Json.JsonElement je ? je.GetDecimal() : Convert.ToDecimal(d[key]);

    private static string Str(Dictionary<string, object> d, string key) =>
        d[key] is System.Text.Json.JsonElement je ? je.GetString() ?? "" : Convert.ToString(d[key]) ?? "";

    private static Dictionary<string, object> AsDict(object o) => o switch
    {
        Dictionary<string, object> d => d,
        System.Text.Json.JsonElement je => System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, object>>(je.GetRawText())!,
        _ => System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, object>>(System.Text.Json.JsonSerializer.Serialize(o))!,
    };
}
