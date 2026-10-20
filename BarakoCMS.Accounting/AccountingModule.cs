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

    public string? PackageId => "BarakoCMS.Accounting";

    public string? PackageVersion => ModuleMetadata.VersionOf(GetType());

    public string? Category => ModuleCategory.Business;

    public string? Description => "Double-entry accounts and journal entries";

    public string? Glyph => "landmark";

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

    public async Task SeedAsync(IModuleSeedContext context, CancellationToken ct)
    {
        await context.EnsureRoleAsync(
            AccountantRoleId, AccountingCapabilities.Accountant.Name,
            "Can post journal entries and view the ledger.", ct);

        // The roles that reached these endpoints before the gate asked for a capability get the
        // capability, so turning Auth:LegacyRoleFallback off does not take the module away from
        // them. Core cannot do this: SystemCapabilities.DefaultsFor does not know this module
        // exists. Additive and idempotent, and it skips a role the host never seeded.
        await AccountingCapabilities.Defaults.GrantAsync(context, ct);

        // Accounts and journal entries are content types (content-type-first). Seed their
        // definitions so the schema validator and the admin's generic content UI know their shape.
        // Idempotent: only inserted when absent, so a host that has customised them is left alone.
        await context.EnsureContentTypeAsync(AccountingContentTypes.AccountDefinition(), ct);
        await context.EnsureContentTypeAsync(AccountingContentTypes.JournalEntryDefinition(), ct);
    }

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}
