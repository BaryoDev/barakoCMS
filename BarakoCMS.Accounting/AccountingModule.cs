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
        // Explicit factory, not AddScoped<AccountService>(): the service has both a read/write
        // (IDocumentSession) and a read-only (IQuerySession) constructor, and the container cannot
        // choose between them — it throws at startup validation. Hosts get the writable one.
        services.AddScoped(sp => new AccountService(sp.GetRequiredService<IDocumentSession>()));
        services.AddScoped<LedgerService>();
        services.AddScoped<ReportingService>();

        // The accounting invariants, contributed into the generic content write pipeline. This is
        // what lets accounts and journal entries be ordinary content types (the project's
        // content-type-first rule) without giving up the balance and chart guarantees.
        services.AddScoped<barakoCMS.Core.Interfaces.IContentLifecycleHook, AccountHook>();
        services.AddScoped<barakoCMS.Core.Interfaces.IContentLifecycleHook, JournalEntryHook>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<Account>()
            .DocumentAlias("accounting_accounts")
            .Index(x => x.Code, idx => idx.IsUnique = true)
            .Index(x => x.Type)
            .Index(x => x.ParentCode)
            .Index(x => x.MemberId);

        schema.For<JournalEntry>()
            .DocumentAlias("accounting_journal_entries")
            .Index(x => x.Date)
            .Index(x => x.EntryNumber, idx => idx.IsUnique = true)
            .Index(x => x.Status)
            .Index(x => x.Reference);

        schema.For<NumberSequence>()
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

        // The roles that reached these endpoints before the gate asked for a capability get the
        // capability, so turning Auth:LegacyRoleFallback off does not take the module away from
        // them. Core cannot do this: SystemCapabilities.DefaultsFor does not know this module
        // exists. Additive and idempotent, and it skips a role the host never seeded.
        await ModuleCapabilities.GrantAsync(
            session, AccountingCapabilities.SeededRoles, AccountingCapabilities.All, ct);

        // Accounts and journal entries are content types (content-type-first). Seed their
        // definitions so the schema validator and the admin's generic content UI know their shape.
        // Idempotent: only inserted when absent, so a host that has customised them is left alone.
        await SeedContentTypeAsync(session, AccountingContentTypes.AccountDefinition(), ct);
        await SeedContentTypeAsync(session, AccountingContentTypes.JournalEntryDefinition(), ct);
    }

    private static async Task SeedContentTypeAsync(
        IDocumentSession session, ContentTypeDefinition definition, CancellationToken ct)
    {
        var existing = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(t => t.Name == definition.Name, ct);
        if (existing is null)
            session.Store(definition);
    }
}
