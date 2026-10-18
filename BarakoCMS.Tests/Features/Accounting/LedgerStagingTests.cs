using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// <c>LedgerService.StageAsync</c> posts a journal entry on the caller's session and leaves the save to
/// the caller, so one request can stage several entries and its own documents and commit them together.
/// </summary>
[Collection("Sequential")]
public class LedgerStagingTests
{
    private readonly IntegrationTestFixture _factory;

    public LedgerStagingTests(IntegrationTestFixture factory) => _factory = factory;

    private static PostEntryCommand Entry(string reference, string debit, string credit, decimal amount) =>
        new(new DateOnly(2026, 3, 1), $"entry {reference}", new[]
        {
            new PostLine(debit, amount, 0m),
            new PostLine(credit, 0m, amount),
        }, reference);

    /// <summary>Stores a cash and an income account, committed, and returns their codes.</summary>
    private async Task<(string Cash, string Income)> SeedChartAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        foreach (var def in new[]
                 {
                     AccountingContentTypes.AccountDefinition(),
                     AccountingContentTypes.JournalEntryDefinition(),
                 })
        {
            var existing = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(t => t.Name == def.Name);
            if (existing is null) session.Store(def);
        }

        var accounts = new AccountService(session);
        await accounts.UpsertAsync(new Account { Code = $"1000-{tag}", Name = "Cash", Type = AccountType.Asset, IsActive = true });
        await accounts.UpsertAsync(new Account { Code = $"4000-{tag}", Name = "Income", Type = AccountType.Income, IsActive = true });
        await session.SaveChangesAsync();
        return ($"1000-{tag}", $"4000-{tag}");
    }

    private async Task<int> StoredEntryCountAsync(string reference)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var all = await query.Query<Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .ToListAsync();
        return all.Count(c => c.Data.TryGetValue("Reference", out var r) && r?.ToString() == reference);
    }

    private async Task<bool> AccountStoredAsync(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await new AccountService(query).GetByCodeAsync(code) is not null;
    }

    private static int Sequence(string entryNumber) => int.Parse(entryNumber[(entryNumber.LastIndexOf('-') + 1)..]);

    [Fact]
    public async Task A_staged_entry_is_not_stored_until_the_caller_saves()
    {
        var (cash, income) = await SeedChartAsync();
        var reference = $"stage-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();

            var staged = await ledger.StageAsync(Entry(reference, cash, income, 100m), Guid.NewGuid(), CancellationToken.None);

            staged.Ok.Should().BeTrue(string.Join("; ", staged.Errors));
            staged.Entry!.EntryNumber.Should().NotBeNullOrWhiteSpace();
            (await StoredEntryCountAsync(reference)).Should().Be(0, "StageAsync must not save");

            await session.SaveChangesAsync();
        }

        (await StoredEntryCountAsync(reference)).Should().Be(1);
    }

    [Fact]
    public async Task Two_staged_entries_and_the_callers_own_document_commit_in_one_save_with_consecutive_numbers()
    {
        var (cash, income) = await SeedChartAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var first = $"first-{tag}";
        var second = $"second-{tag}";
        var ownCode = $"9000-{tag}";
        string firstNumber, secondNumber;

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();

            var a = await ledger.StageAsync(Entry(first, cash, income, 100m), Guid.NewGuid(), CancellationToken.None);
            var b = await ledger.StageAsync(Entry(second, cash, income, 40m), Guid.NewGuid(), CancellationToken.None);
            await new AccountService(session).UpsertAsync(
                new Account { Code = ownCode, Name = "Caller document", Type = AccountType.Asset, IsActive = true });

            a.Ok.Should().BeTrue(string.Join("; ", a.Errors));
            b.Ok.Should().BeTrue(string.Join("; ", b.Errors));
            firstNumber = a.Entry!.EntryNumber;
            secondNumber = b.Entry!.EntryNumber;

            (await StoredEntryCountAsync(first)).Should().Be(0);
            (await StoredEntryCountAsync(second)).Should().Be(0);
            (await AccountStoredAsync(ownCode)).Should().BeFalse();

            await session.SaveChangesAsync();
        }

        (await StoredEntryCountAsync(first)).Should().Be(1);
        (await StoredEntryCountAsync(second)).Should().Be(1);
        (await AccountStoredAsync(ownCode)).Should().BeTrue();
        Sequence(secondNumber).Should().Be(Sequence(firstNumber) + 1, "two entries staged on one session must not repeat a number");
    }
}
