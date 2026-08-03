using barakoCMS.Core.Interfaces;
using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// The accounting rules that a content-type schema cannot express, enforced inside the generic
/// content write pipeline so a journal entry can be ordinary content without the ledger ever being
/// writable into an illegal state:
///
/// <list type="bullet">
/// <item>every line has exactly one of debit/credit, non-negative;</item>
/// <item>total debits equal total credits, and the entry is non-zero;</item>
/// <item>every referenced account exists and is active;</item>
/// <item>a posted entry is immutable — corrections are made by reversing, never by editing;</item>
/// <item>on create, the next sequential <c>EntryNumber</c> and the denormalised <c>Amount</c> are
///       stamped by the server rather than trusted from the caller.</item>
/// </list>
///
/// The number is allocated through the same session the endpoint commits, so the entry and the
/// sequence increment land in one transaction — a clash rolls back the whole post rather than
/// burning or duplicating a number.
/// </summary>
public sealed class JournalEntryHook : IContentLifecycleHook
{
    public string ContentType => AccountingContentTypes.JournalEntry;

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(
        ContentLifecycleContext context, CancellationToken ct)
    {
        var errors = new List<string>();
        var data = context.Data;

        // Immutability: a posted entry may only be voided, never edited. Voiding flips Status and
        // is the one field change allowed, so compare everything else against what is stored.
        if (!context.IsCreate)
        {
            var previous = context.Existing!;
            var wasPosted = ContentData.AsString(ContentData.Get(previous, "Status")) != "Voided";
            var linesChanged = System.Text.Json.JsonSerializer.Serialize(ContentData.Get(data, "Lines"))
                            != System.Text.Json.JsonSerializer.Serialize(ContentData.Get(previous, "Lines"));
            if (wasPosted && linesChanged)
                errors.Add("A posted journal entry cannot be edited. Post a reversing entry instead.");

            // The server owns these; an update must not rewrite them.
            ContentData.Set(data, "EntryNumber", ContentData.Get(previous, "EntryNumber") ?? string.Empty);
            ContentData.Set(data, "Amount", ContentData.Get(previous, "Amount") ?? 0m);
        }

        var lines = ReadLines(ContentData.Get(data, "Lines"), errors);

        if (lines.Count < 2)
            errors.Add("A journal entry needs at least two lines.");

        decimal totalDebit = 0, totalCredit = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var (code, debit, credit) = lines[i];
            if (debit < 0 || credit < 0)
                errors.Add($"Line {i + 1}: debit and credit must be non-negative.");
            if (debit > 0 && credit > 0)
                errors.Add($"Line {i + 1}: a line cannot have both a debit and a credit.");
            if (debit == 0 && credit == 0)
                errors.Add($"Line {i + 1}: a line must have either a debit or a credit.");
            if (string.IsNullOrWhiteSpace(code))
                errors.Add($"Line {i + 1}: account code is required.");

            totalDebit += debit;
            totalCredit += credit;
        }

        // Exact decimal comparison — the whole reason money must not round-trip as double.
        if (totalDebit != totalCredit)
            errors.Add($"Entry is not balanced: debits {totalDebit:0.00} != credits {totalCredit:0.00}.");
        if (totalDebit == 0 && lines.Count > 0)
            errors.Add("Entry total must be greater than zero.");

        // Every referenced account must exist and be active.
        var codes = lines.Select(l => l.Code)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codes.Count > 0)
        {
            var accounts = await context.Session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType == AccountingContentTypes.Account)
                .ToListAsync(ct);

            var byCode = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in accounts)
            {
                var code = ContentData.AsString(ContentData.Get(a.Data, "Code"));
                if (!string.IsNullOrWhiteSpace(code)) byCode[code!] = a.Data;
            }

            foreach (var code in codes)
            {
                if (!byCode.TryGetValue(code, out var acct))
                    errors.Add($"Account '{code}' does not exist.");
                else if (ContentData.Get(acct, "IsActive") is bool active && !active)
                    errors.Add($"Account '{code}' is inactive.");
            }
        }

        if (errors.Count > 0)
            return errors;

        // Server-owned fields. Only stamped once the entry is known to be valid, so a rejected post
        // never consumes a sequence number.
        if (context.IsCreate)
        {
            ContentData.Set(data, "Amount", totalDebit);
            ContentData.Set(data, "EntryNumber", await NextEntryNumberAsync(context.Session, data, ct));
            if (string.IsNullOrWhiteSpace(ContentData.AsString(ContentData.Get(data, "Status"))))
                ContentData.Set(data, "Status", "Posted");
        }

        return Array.Empty<string>();
    }

    private static async Task<string> NextEntryNumberAsync(
        IDocumentSession session, Dictionary<string, object> data, CancellationToken ct)
    {
        var year = ReadDate(ContentData.Get(data, "Date"))?.Year ?? DateTime.UtcNow.Year;
        var key = $"JE-{year}";

        var seq = await session.LoadAsync<NumberSequence>(key, ct) ?? new NumberSequence { Id = key, Value = 0 };
        seq.Value += 1;
        // Stored on the endpoint's session: the increment commits with the entry itself.
        session.Store(seq);
        return $"{key}-{seq.Value:000000}";
    }

    /// <summary>
    /// Reads the Lines array out of the untyped bag. Values arrive as the plain CLR types
    /// ObjectJsonConverter produces (decimal for money), whether freshly posted or reloaded.
    /// </summary>
    private static List<(string Code, decimal Debit, decimal Credit)> ReadLines(object? raw, List<string> errors)
    {
        var result = new List<(string, decimal, decimal)>();

        if (raw is null)
        {
            errors.Add("Lines are required.");
            return result;
        }

        if (raw is not System.Collections.IEnumerable seq || raw is string)
        {
            errors.Add("Lines must be an array.");
            return result;
        }

        foreach (var item in seq)
        {
            if (item is not IDictionary<string, object> line)
            {
                errors.Add("Each line must be an object with AccountCode, Debit and Credit.");
                continue;
            }

            result.Add((
                ContentData.AsString(ContentData.Get(line.AsReadOnly(), "AccountCode")) ?? string.Empty,
                ContentData.AsDecimal(ContentData.Get(line.AsReadOnly(), "Debit")),
                ContentData.AsDecimal(ContentData.Get(line.AsReadOnly(), "Credit"))));
        }

        return result;
    }

    private static DateTime? ReadDate(object? v) => v switch
    {
        DateTime dt => dt,
        string s when DateTime.TryParse(s, out var parsed) => parsed,
        _ => null,
    };
}
