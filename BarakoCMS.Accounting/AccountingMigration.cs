using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Moves an existing deployment's accounting data from the old strongly-typed Marten documents
/// (<see cref="Account"/> / <see cref="JournalEntry"/>) onto the content types the module now uses.
///
/// Deliberately <b>copy, not move</b>: the original documents are left exactly where they are. If
/// anything about the converted shape turns out wrong, the source of truth is still on disk and the
/// migration can simply be re-run after fixing it. Deleting the originals is a separate, later
/// decision for the operator — this is a ledger, and nothing here should be the step that loses it.
///
/// Idempotent: an entry already present (matched on account Code / journal EntryNumber) is skipped,
/// so running it twice does not duplicate the chart or double-post the ledger.
/// </summary>
public static class AccountingMigration
{
    public sealed record Result(int AccountsCopied, int AccountsSkipped, int EntriesCopied, int EntriesSkipped)
    {
        public int TotalCopied => AccountsCopied + EntriesCopied;
    }

    public static async Task<Result> RunAsync(IDocumentSession session, Guid migratedBy, CancellationToken ct = default)
    {
        var existingContent = await session.Query<barakoCMS.Models.Content>()
            .Where(c => c.ContentType == AccountingContentTypes.Account
                     || c.ContentType == AccountingContentTypes.JournalEntry)
            .ToListAsync(ct);

        var haveAccountCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var haveEntryNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in existingContent)
        {
            if (c.ContentType == AccountingContentTypes.Account)
            {
                var code = ContentData.AsString(ContentData.Get(c.Data, "Code"));
                if (!string.IsNullOrWhiteSpace(code)) haveAccountCodes.Add(code!);
            }
            else
            {
                var number = ContentData.AsString(ContentData.Get(c.Data, "EntryNumber"));
                if (!string.IsNullOrWhiteSpace(number)) haveEntryNumbers.Add(number!);
            }
        }

        int accountsCopied = 0, accountsSkipped = 0, entriesCopied = 0, entriesSkipped = 0;

        foreach (var account in await session.Query<Account>().ToListAsync(ct))
        {
            if (haveAccountCodes.Contains(account.Code)) { accountsSkipped++; continue; }

            session.Store(NewContent(AccountingContentTypes.Account, new Dictionary<string, object>
            {
                ["Code"] = account.Code,
                ["Name"] = account.Name,
                ["Type"] = account.Type.ToString(),
                ["ParentCode"] = account.ParentCode ?? string.Empty,
                ["MemberId"] = account.MemberId?.ToString() ?? string.Empty,
                ["PayeeName"] = account.PayeeName ?? string.Empty,
                ["IsActive"] = account.IsActive,
            }, account.CreatedAt));
            accountsCopied++;
        }

        foreach (var entry in await session.Query<JournalEntry>().ToListAsync(ct))
        {
            if (haveEntryNumbers.Contains(entry.EntryNumber)) { entriesSkipped++; continue; }

            var lines = entry.Lines
                .Select(l => (object)new Dictionary<string, object>
                {
                    ["AccountCode"] = l.AccountCode,
                    // Stays decimal end to end — the reason the serializer was fixed first.
                    ["Debit"] = l.Debit,
                    ["Credit"] = l.Credit,
                    ["Memo"] = l.Memo ?? string.Empty,
                })
                .ToList();

            session.Store(NewContent(AccountingContentTypes.JournalEntry, new Dictionary<string, object>
            {
                ["EntryNumber"] = entry.EntryNumber,
                ["Date"] = entry.Date.ToString("yyyy-MM-dd"),
                ["Memo"] = entry.Memo,
                ["Reference"] = entry.Reference ?? string.Empty,
                ["Lines"] = lines,
                ["Status"] = entry.Status.ToString(),
                ["VoidsEntryId"] = entry.VoidsEntryId?.ToString() ?? string.Empty,
                ["Amount"] = entry.Amount,
                ["Attachments"] = entry.Attachments.Cast<object>().ToList(),
            }, entry.CreatedAt));
            entriesCopied++;
        }

        await session.SaveChangesAsync(ct);
        return new Result(accountsCopied, accountsSkipped, entriesCopied, entriesSkipped);
    }

    /// <summary>
    /// Builds the read-model document directly rather than appending a ContentCreated event: this is
    /// a data move, not a new authoring action, and replaying it as authored events would put a
    /// migration's worth of noise into every entry's history.
    /// </summary>
    private static barakoCMS.Models.Content NewContent(
        string contentType, Dictionary<string, object> data, DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = contentType,
        Status = barakoCMS.Models.ContentStatus.Published,
        Sensitivity = barakoCMS.Models.SensitivityLevel.Public,
        Data = data,
        // Preserve the original timestamps: an accounting record's date is part of the record.
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
}
