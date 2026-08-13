using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Read/write access to the chart of accounts for host applications.
///
/// Accounts are stored as content now, but a host should not have to know that: hand-building the
/// data dictionary at every call site would spread the storage shape across every consumer and make
/// a future change to it a breaking change for all of them. Callers keep working with
/// <see cref="Account"/>; this service owns the translation.
///
/// Registered by <see cref="AccountingModule"/>, so any host with the module gets it from DI.
/// </summary>
public class AccountService
{
    private readonly IQuerySession _query;
    private readonly IDocumentSession? _write;

    /// <summary>Read and write.</summary>
    public AccountService(IDocumentSession session)
    {
        _query = session;
        _write = session;
    }

    /// <summary>
    /// Read-only, for callers that only hold an <see cref="IQuerySession"/>. Calling
    /// <see cref="UpsertAsync"/> on one of these throws rather than silently doing nothing.
    /// </summary>
    public AccountService(IQuerySession session)
    {
        _query = session;
        _write = null;
    }

    private IDocumentSession WriteSession => _write
        ?? throw new InvalidOperationException(
            "This AccountService was built on a read-only IQuerySession; it cannot write accounts.");

    /// <summary>The whole chart, ordered by code.</summary>
    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default) =>
        (await AccountingContentReader.AccountsAsync(_query, ct))
        .OrderBy(a => a.Code, StringComparer.Ordinal)
        .ToList();

    public async Task<Account?> GetByCodeAsync(string code, CancellationToken ct = default) =>
        (await AccountingContentReader.AccountsAsync(_query, ct))
        .FirstOrDefault(a => string.Equals(a.Code, code, StringComparison.OrdinalIgnoreCase));

    public async Task<int> CountAsync(CancellationToken ct = default) =>
        (await AccountingContentReader.AccountsAsync(_query, ct)).Count;

    /// <summary>
    /// Creates the account if its code is new, otherwise updates the existing one in place. Does not
    /// commit — the caller owns the transaction, matching the rest of the module, so a host can
    /// seed a whole chart in one unit of work.
    /// </summary>
    public async Task UpsertAsync(Account account, CancellationToken ct = default)
    {
        var session = WriteSession;
        var data = ToData(account);

        // Accounts staged earlier in this same unit of work are not in the database yet, so a query
        // cannot see them. Seeding a whole chart in one transaction is the ordinary way to reach
        // that, and without this the second appearance of a code becomes a second account — one code
        // split across two documents, with lookups picking between them arbitrarily.
        var staged = session.PendingChanges.AllChangedFor<barakoCMS.Models.Content>()
            .FirstOrDefault(c => c.ContentType == AccountingContentTypes.Account && HasCode(c, account.Code));

        if (staged is not null)
        {
            staged.Data = data;
            staged.UpdatedAt = DateTime.UtcNow;
            session.Store(staged);
            return;
        }

        var existing = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == AccountingContentTypes.Account)
            .ToListAsync(ct);

        var match = existing.FirstOrDefault(c => HasCode(c, account.Code));

        if (match is not null)
        {
            match.Data = data;
            match.UpdatedAt = DateTime.UtcNow;
            session.Store(match);
            return;
        }

        session.Store(new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = AccountingContentTypes.Account,
            Status = barakoCMS.Models.ContentStatus.Published,
            Sensitivity = barakoCMS.Models.SensitivityLevel.Public,
            Data = data,
            CreatedAt = account.CreatedAt == default ? DateTime.UtcNow : account.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>Convenience for seeding: upserts many accounts into one unit of work.</summary>
    public async Task UpsertManyAsync(IEnumerable<Account> accounts, CancellationToken ct = default)
    {
        foreach (var account in accounts)
            await UpsertAsync(account, ct);
    }

    private static bool HasCode(barakoCMS.Models.Content c, string code) => string.Equals(
        ContentData.AsString(ContentData.Get(c.Data, "Code")), code, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object> ToData(Account a) => new()
    {
        ["Code"] = a.Code,
        ["Name"] = a.Name,
        ["Type"] = a.Type.ToString(),
        ["ParentCode"] = a.ParentCode ?? string.Empty,
        ["MemberId"] = a.MemberId?.ToString() ?? string.Empty,
        ["PayeeName"] = a.PayeeName ?? string.Empty,
        ["IsActive"] = a.IsActive,
    };
}
