using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>Seeds the chart and entries the accounting action and report tests read.</summary>
internal static class AccountingTestData
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A code no other test uses, so balances can be asserted without other tests' postings.</summary>
    public static string Code(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..14];

    public static async Task EnsureTypesAsync(IntegrationTestFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var def in new[] { AccountingContentTypes.AccountDefinition(), AccountingContentTypes.JournalEntryDefinition() })
        {
            if (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(t => t.Name == def.Name, Ct) is null)
                session.Store(def);
        }

        await session.SaveChangesAsync(Ct);
    }

    public static async Task<string> AccountAsync(IntegrationTestFixture fixture, string prefix, AccountType type)
    {
        await EnsureTypesAsync(fixture);
        var code = Code(prefix);
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await new AccountService(session).UpsertAsync(
            new Account { Code = code, Name = $"Account {code}", Type = type, IsActive = true }, Ct);
        await session.SaveChangesAsync(Ct);
        return code;
    }

    /// <summary>Posts a two line entry through the ledger service and returns the stored entry's id.</summary>
    public static async Task<Guid> PostAsync(
        IntegrationTestFixture fixture, DateOnly date, string debit, string credit, decimal amount, string? reference = null)
    {
        using var scope = fixture.Services.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<LedgerService>();
        var result = await ledger.PostAsync(
            new PostEntryCommand(date, "test posting",
            [
                new PostLine(debit, amount, 0m),
                new PostLine(credit, 0m, amount),
            ], reference), Guid.NewGuid(), Ct);
        if (!result.Ok) throw new InvalidOperationException(string.Join("; ", result.Errors));
        return result.Entry!.Id;
    }

    public static async Task<Content> LoadAsync(IntegrationTestFixture fixture, Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct))!;
    }

    /// <summary>Every stored journal entry whose reference starts with the prefix.</summary>
    public static async Task<List<Content>> EntriesAsync(IntegrationTestFixture fixture, string referencePrefix)
    {
        using var scope = fixture.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .ToListAsync(Ct);
        return all.Where(c => c.Data.TryGetValue("Reference", out var r)
                              && r?.ToString()?.StartsWith(referencePrefix, StringComparison.Ordinal) == true).ToList();
    }

    /// <summary>A text field of an entry's data, or empty.</summary>
    public static string Text(Content entry, string field) =>
        entry.Data.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value?.ToString() ?? string.Empty;

    /// <summary>The account, debit and credit of each line of an entry.</summary>
    public static List<(string Account, decimal Debit, decimal Credit)> Lines(Content entry) =>
        ((System.Collections.IEnumerable)entry.Data["Lines"]).Cast<IDictionary<string, object>>()
        .Select(l => (l["AccountCode"].ToString()!, Convert.ToDecimal(l["Debit"]), Convert.ToDecimal(l["Credit"])))
        .ToList();
}
