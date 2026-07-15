using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

public record AccountBalance(string Code, string Name, AccountType Type, string? ParentCode, decimal Balance);

public record LedgerLine(DateOnly Date, string EntryNumber, string Memo, decimal Debit, decimal Credit, decimal RunningBalance, IReadOnlyList<string> Attachments);

public record AccountLedger(string AccountCode, string AccountName, Guid? MemberId, decimal Balance, IReadOnlyList<LedgerLine> Lines);

public record CashFlow(string Period, decimal Opening, decimal Receipts, decimal Disbursements, decimal Closing, IReadOnlyList<LedgerLine> Lines);

/// <summary>
/// Computes balances and ledgers from posted journal entries. Generic: it knows nothing about any
/// particular chart of accounts.
///
/// Lines are embedded in each entry, so aggregation is done in memory after loading posted entries.
/// At small-to-moderate volumes this is simple and correct. If volume ever demands it, the upgrade
/// path is a Marten projection maintaining an AccountBalance read-model — no API change, since reports
/// already flow through this service.
/// </summary>
public class ReportingService
{
    private readonly IQuerySession _session;

    public ReportingService(IQuerySession session) => _session = session;

    private async Task<List<JournalEntry>> PostedEntriesAsync(DateOnly? asOf, CancellationToken ct)
    {
        var q = _session.Query<JournalEntry>().Where(e => e.Status == JournalStatus.Posted);
        if (asOf is { } d) q = q.Where(e => e.Date <= d);
        var list = await q.ToListAsync(ct);
        return list.ToList();
    }

    /// <summary>Signed balance per account, using each account's normal side. A trial balance.</summary>
    public async Task<List<AccountBalance>> BalancesAsync(DateOnly? asOf, CancellationToken ct)
    {
        var accounts = await _session.Query<Account>().ToListAsync(ct);
        var entries = await PostedEntriesAsync(asOf, ct);

        var debit = new Dictionary<string, decimal>();
        var credit = new Dictionary<string, decimal>();
        foreach (var line in entries.SelectMany(e => e.Lines))
        {
            debit[line.AccountCode] = debit.GetValueOrDefault(line.AccountCode) + line.Debit;
            credit[line.AccountCode] = credit.GetValueOrDefault(line.AccountCode) + line.Credit;
        }

        return accounts
            .OrderBy(a => a.Code)
            .Select(a =>
            {
                var d = debit.GetValueOrDefault(a.Code);
                var c = credit.GetValueOrDefault(a.Code);
                var bal = a.IsDebitNormal ? d - c : c - d;
                return new AccountBalance(a.Code, a.Name, a.Type, a.ParentCode, bal);
            })
            .ToList();
    }

    /// <summary>A single account's ledger with a running balance (e.g. a member's receivable).</summary>
    public async Task<AccountLedger?> AccountLedgerAsync(string accountCode, CancellationToken ct)
    {
        var account = await _session.Query<Account>().FirstOrDefaultAsync(a => a.Code == accountCode, ct);
        if (account is null) return null;

        var entries = await PostedEntriesAsync(null, ct);
        var rows = entries
            .SelectMany(e => e.Lines.Where(l => l.AccountCode == accountCode)
                .Select(l => (e.Date, e.EntryNumber, Memo: l.Memo ?? e.Memo, l.Debit, l.Credit,
                    Attachments: (IReadOnlyList<string>)e.Attachments)))
            .OrderBy(x => x.Date).ThenBy(x => x.EntryNumber)
            .ToList();

        var running = 0m;
        var lines = new List<LedgerLine>();
        foreach (var r in rows)
        {
            running += account.IsDebitNormal ? r.Debit - r.Credit : r.Credit - r.Debit;
            lines.Add(new LedgerLine(r.Date, r.EntryNumber, r.Memo, r.Debit, r.Credit, running, r.Attachments));
        }

        return new AccountLedger(account.Code, account.Name, account.MemberId, running, lines);
    }

    /// <summary>
    /// Cash movement through the caller-supplied cash accounts for a month. The set of "cash" accounts
    /// is a policy the caller owns, so this stays generic.
    /// </summary>
    public async Task<CashFlow> CashFlowAsync(IReadOnlyCollection<string> cashCodes, int year, int month, CancellationToken ct)
    {
        var start = new DateOnly(year, month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        var entries = await PostedEntriesAsync(end, ct);

        decimal opening = 0, receipts = 0, disbursements = 0;
        var lines = new List<LedgerLine>();

        foreach (var e in entries.OrderBy(e => e.Date).ThenBy(e => e.EntryNumber))
        foreach (var l in e.Lines.Where(l => cashCodes.Contains(l.AccountCode)))
        {
            var delta = l.Debit - l.Credit; // cash is debit-normal
            if (e.Date < start)
            {
                opening += delta;
            }
            else
            {
                if (delta >= 0) receipts += delta; else disbursements += -delta;
                lines.Add(new LedgerLine(e.Date, e.EntryNumber, l.Memo ?? e.Memo, l.Debit, l.Credit, opening + receipts - disbursements, e.Attachments));
            }
        }

        return new CashFlow($"{year:0000}-{month:00}", opening, receipts, disbursements, opening + receipts - disbursements, lines);
    }
}
