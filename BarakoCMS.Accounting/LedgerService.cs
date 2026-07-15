using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>A line to post, in the caller's terms (account code + debit/credit).</summary>
public record PostLine(string AccountCode, decimal Debit, decimal Credit, string? Memo = null);

/// <summary>A request to post one balanced journal entry.</summary>
public record PostEntryCommand(
    DateOnly Date,
    string Memo,
    IReadOnlyList<PostLine> Lines,
    string? Reference = null,
    Guid? VoidsEntryId = null,
    IReadOnlyList<string>? Attachments = null);

/// <summary>Outcome of a post attempt: either the stored entry or a list of validation errors.</summary>
public record PostResult(JournalEntry? Entry, IReadOnlyList<string> Errors)
{
    public bool Ok => Entry is not null;
    public static PostResult Fail(params string[] errors) => new(null, errors);
    public static PostResult Success(JournalEntry entry) => new(entry, Array.Empty<string>());
}

/// <summary>
/// Posts balanced double-entry journal entries. The balance invariant (total debits == total
/// credits) and account existence are enforced here, in the backend, before anything is written —
/// this is the one accounting rule barakoCMS's generic content validation cannot express.
/// </summary>
public class LedgerService
{
    private readonly IDocumentSession _session;

    public LedgerService(IDocumentSession session) => _session = session;

    public async Task<PostResult> PostAsync(PostEntryCommand cmd, Guid userId, CancellationToken ct)
    {
        var errors = new List<string>();

        if (cmd.Lines.Count < 2)
            errors.Add("A journal entry needs at least two lines.");

        foreach (var (line, i) in cmd.Lines.Select((l, i) => (l, i)))
        {
            if (line.Debit < 0 || line.Credit < 0)
                errors.Add($"Line {i + 1}: debit and credit must be non-negative.");
            if (line.Debit > 0 && line.Credit > 0)
                errors.Add($"Line {i + 1}: a line cannot have both a debit and a credit.");
            if (line.Debit == 0 && line.Credit == 0)
                errors.Add($"Line {i + 1}: a line must have either a debit or a credit.");
            if (string.IsNullOrWhiteSpace(line.AccountCode))
                errors.Add($"Line {i + 1}: account code is required.");
        }

        var totalDebit = cmd.Lines.Sum(l => l.Debit);
        var totalCredit = cmd.Lines.Sum(l => l.Credit);
        if (totalDebit != totalCredit)
            errors.Add($"Entry is not balanced: debits {totalDebit:0.00} != credits {totalCredit:0.00}.");
        if (totalDebit == 0)
            errors.Add("Entry total must be greater than zero.");

        // Verify every referenced account exists and is active.
        var codes = cmd.Lines.Select(l => l.AccountCode).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToList();
        if (codes.Count > 0)
        {
            var accounts = await _session.Query<Account>()
                .Where(a => codes.Contains(a.Code))
                .ToListAsync(ct);
            var found = accounts.ToDictionary(a => a.Code);
            foreach (var code in codes)
            {
                if (!found.TryGetValue(code, out var acct))
                    errors.Add($"Account '{code}' does not exist.");
                else if (!acct.IsActive)
                    errors.Add($"Account '{code}' is inactive.");
            }
        }

        if (errors.Count > 0)
            return new PostResult(null, errors);

        var entry = new JournalEntry
        {
            EntryNumber = await NextEntryNumberAsync(cmd.Date, ct),
            Date = cmd.Date,
            Memo = cmd.Memo,
            Reference = cmd.Reference,
            VoidsEntryId = cmd.VoidsEntryId,
            Status = JournalStatus.Posted,
            Amount = totalDebit,
            Attachments = cmd.Attachments?.ToList() ?? new List<string>(),
            CreatedBy = userId,
            Lines = cmd.Lines
                .Select(l => new JournalLine { AccountCode = l.AccountCode, Debit = l.Debit, Credit = l.Credit, Memo = l.Memo })
                .ToList()
        };

        _session.Store(entry);
        // The entry and the sequence increment commit together; a concurrency clash on the
        // sequence rolls back the whole post so no number is ever skipped or duplicated.
        await _session.SaveChangesAsync(ct);

        return PostResult.Success(entry);
    }

    private async Task<string> NextEntryNumberAsync(DateOnly date, CancellationToken ct)
    {
        var key = $"JE-{date.Year}";
        var seq = await _session.LoadAsync<NumberSequence>(key, ct);
        if (seq is null)
        {
            seq = new NumberSequence { Id = key, Value = 0 };
        }
        seq.Value += 1;
        _session.Store(seq);
        return $"{key}-{seq.Value:000000}";
    }
}
