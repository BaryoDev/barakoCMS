using BarakoCMS.Accounting.Domain;
using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Weasel.Core;

namespace BarakoCMS.Accounting;

/// <summary>
/// Optional double-entry accounting module for barakoCMS. A host enables it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new AccountingModule()));</code>
/// It contributes the ledger services, the accounting document schema, its endpoints, and a
/// baseline "Accountant" role. It is chart-of-accounts agnostic — the host defines the chart.
/// </summary>
public sealed class AccountingModule : IBarakoModule
{
    /// <summary>Deterministic id for the baseline Accountant role this module seeds.</summary>
    public static readonly Guid AccountantRoleId = Guid.Parse("00000000-0000-0000-0000-0000ACC00001");

    public string Name => "Accounting";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<LedgerService>();
        services.AddScoped<ReportingService>();
    }

    public void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<Account>()
            .DocumentAlias("accounting_accounts")
            .Index(x => x.Code, idx => idx.IsUnique = true)
            .Index(x => x.Type)
            .Index(x => x.ParentCode)
            .Index(x => x.MemberId);

        options.Schema.For<JournalEntry>()
            .DocumentAlias("accounting_journal_entries")
            .Index(x => x.Date)
            .Index(x => x.EntryNumber, idx => idx.IsUnique = true)
            .Index(x => x.Status)
            .Index(x => x.Reference);

        options.Schema.For<NumberSequence>()
            .DocumentAlias("accounting_number_sequences")
            .UseOptimisticConcurrency(true);
    }

    public async Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
        var existing = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "Accountant", ct);
        if (existing is null)
        {
            session.Store(new Role
            {
                Id = AccountantRoleId,
                Name = "Accountant",
                Description = "Can post journal entries and view the ledger."
            });
        }
    }
}
