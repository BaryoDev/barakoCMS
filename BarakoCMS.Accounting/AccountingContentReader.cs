using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Reads accounts and journal entries out of the content store and projects them back into the
/// module's domain shapes.
///
/// Accounting is now modelled as content types, but everything downstream of loading — the trial
/// balance, the running-balance ledger, cash flow — is arithmetic that was already correct and
/// already tested. Rather than rewrite that math against untyped dictionaries (and risk the numbers
/// in the one module where wrong numbers matter most), only the data source moves: this reader turns
/// content back into <see cref="Account"/> and <see cref="JournalEntry"/>, and the aggregation code
/// is untouched.
///
/// Money comes back as <see cref="decimal"/> rather than <see cref="double"/> — see
/// <c>ObjectJsonConverter</c>, which is what made storing a ledger in a JSON bag defensible.
/// </summary>
internal static class AccountingContentReader
{
    public static async Task<List<Account>> AccountsAsync(IQuerySession session, CancellationToken ct)
    {
        var rows = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == AccountingContentTypes.Account)
            .ToListAsync(ct);

        return rows.Select(r => ToAccount(r.Data)).Where(a => !string.IsNullOrWhiteSpace(a.Code)).ToList();
    }

    public static async Task<List<JournalEntry>> PostedEntriesAsync(
        IQuerySession session, DateOnly? asOf, CancellationToken ct)
    {
        var rows = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .ToListAsync(ct);

        // Status and Date live inside the JSON bag, so filtering happens here rather than in SQL.
        // Same shape as the previous implementation, which also materialised posted entries before
        // aggregating; roadmap U.18 tracks pushing this into a projection.
        return rows
            .Select(r => ToEntry(r.Data))
            .Where(e => e.Status == JournalStatus.Posted)
            .Where(e => asOf is not { } d || e.Date <= d)
            .ToList();
    }

    private static Account ToAccount(Dictionary<string, object> data) => new()
    {
        Code = ContentData.AsString(ContentData.Get(data, "Code")) ?? string.Empty,
        Name = ContentData.AsString(ContentData.Get(data, "Name")) ?? string.Empty,
        Type = Enum.TryParse<AccountType>(ContentData.AsString(ContentData.Get(data, "Type")), true, out var t)
            ? t
            : AccountType.Asset,
        ParentCode = NullIfBlank(ContentData.AsString(ContentData.Get(data, "ParentCode"))),
        MemberId = Guid.TryParse(ContentData.AsString(ContentData.Get(data, "MemberId")), out var m) ? m : null,
        PayeeName = NullIfBlank(ContentData.AsString(ContentData.Get(data, "PayeeName"))),
        IsActive = ContentData.Get(data, "IsActive") is not bool active || active,
    };

    private static JournalEntry ToEntry(Dictionary<string, object> data) => new()
    {
        EntryNumber = ContentData.AsString(ContentData.Get(data, "EntryNumber")) ?? string.Empty,
        Date = ReadDate(ContentData.Get(data, "Date")),
        Memo = ContentData.AsString(ContentData.Get(data, "Memo")) ?? string.Empty,
        Reference = NullIfBlank(ContentData.AsString(ContentData.Get(data, "Reference"))),
        Status = Enum.TryParse<JournalStatus>(ContentData.AsString(ContentData.Get(data, "Status")), true, out var s)
            ? s
            : JournalStatus.Posted,
        VoidsEntryId = Guid.TryParse(ContentData.AsString(ContentData.Get(data, "VoidsEntryId")), out var v) ? v : null,
        Amount = ContentData.AsDecimal(ContentData.Get(data, "Amount")),
        Lines = ReadLines(ContentData.Get(data, "Lines")),
        Attachments = ReadStrings(ContentData.Get(data, "Attachments")),
    };

    private static List<JournalLine> ReadLines(object? raw)
    {
        var lines = new List<JournalLine>();
        if (raw is not System.Collections.IEnumerable seq || raw is string) return lines;

        foreach (var item in seq)
        {
            if (item is not IDictionary<string, object> line) continue;
            var readOnly = line.AsReadOnly();
            lines.Add(new JournalLine
            {
                AccountCode = ContentData.AsString(ContentData.Get(readOnly, "AccountCode")) ?? string.Empty,
                Debit = ContentData.AsDecimal(ContentData.Get(readOnly, "Debit")),
                Credit = ContentData.AsDecimal(ContentData.Get(readOnly, "Credit")),
                Memo = NullIfBlank(ContentData.AsString(ContentData.Get(readOnly, "Memo"))),
            });
        }

        return lines;
    }

    private static List<string> ReadStrings(object? raw)
    {
        var result = new List<string>();
        if (raw is not System.Collections.IEnumerable seq || raw is string) return result;
        foreach (var item in seq)
        {
            var s = item?.ToString();
            if (!string.IsNullOrWhiteSpace(s)) result.Add(s!);
        }
        return result;
    }

    private static DateOnly ReadDate(object? v) => v switch
    {
        DateTime dt => DateOnly.FromDateTime(dt),
        DateOnly d => d,
        string s when DateOnly.TryParse(s, out var parsed) => parsed,
        string s when DateTime.TryParse(s, out var parsed) => DateOnly.FromDateTime(parsed),
        _ => default,
    };

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
