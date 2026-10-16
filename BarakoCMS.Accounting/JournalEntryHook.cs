#pragma warning disable CS0618 // needs the write session itself (locks, number allocation, reads of definitions), which IContentHookContext does not offer yet
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
    /// <summary>The core series journal entry numbers are counted in, one count per tenant and year.</summary>
    public static readonly barakoCMS.Core.Interfaces.NumberSeries EntryNumbers =
        new("accounting.je", Prefix: "JE-", Padding: 6, Reset: barakoCMS.Core.Interfaces.NumberReset.Yearly);

    private readonly barakoCMS.Core.Interfaces.INumberAllocator _numbers;

    /// <summary>Numbers entries with the core gapless allocator.</summary>
    public JournalEntryHook(barakoCMS.Core.Interfaces.INumberAllocator numbers)
    {
        _numbers = numbers;
    }

    /// <summary>The form before 5.0, for a caller that constructs the hook. Numbers with the core gapless allocator too.</summary>
    [Obsolete("Construct with an INumberAllocator, as DI does. Removal planned for barakoCMS 6.0.")]
    public JournalEntryHook()
        : this(barakoCMS.Infrastructure.Numbering.NumberAllocators.Gapless())
    {
    }

    public string ContentType => AccountingContentTypes.JournalEntry;

    public Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct) =>
        CheckAsync(context, allowReserved: false, ct);

    internal async Task<IReadOnlyList<string>> CheckAsync(ContentLifecycleContext context, bool allowReserved, CancellationToken ct)
    {
        var errors = new List<string>();
        var data = context.Data;
        if (ReservedReferenceError(context, allowReserved) is { } reserved)
            errors.Add(reserved);

        // Immutability, failing closed: an entry has no draft state, so an update never changes its
        // lines, a voided entry takes no update at all, and Status moves only through the Void
        // transition (which sends neither). The check reads the stored entry, never the caller's data.
        if (!context.IsCreate)
        {
            var previous = context.Existing!;
            errors.AddRange(await ImmutabilityErrorsAsync(context, ct));

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

        errors.AddRange(await AccountErrorsAsync(context, lines, ct));

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

    /// <summary>Every referenced account must exist and be active.</summary>
    private static async Task<List<string>> AccountErrorsAsync(
        ContentLifecycleContext context, List<(string Code, decimal Debit, decimal Credit)> lines, CancellationToken ct)
    {
        var errors = new List<string>();
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

        return errors;
    }

    /// <summary>
    /// A manual entry may not take a reference in the form a workflow posting uses, since the posting
    /// action is keyed by it. The ledger service passes <paramref name="allowReserved"/> for the
    /// entries a workflow run makes.
    /// </summary>
    private static string? ReservedReferenceError(ContentLifecycleContext context, bool allowReserved)
    {
        var reference = ContentData.AsString(ContentData.Get(context.Data, "Reference"));
        var unchanged = !context.IsCreate && reference == ContentData.AsString(ContentData.Get(context.Existing!, "Reference"));
        return !allowReserved && !unchanged && PostingReference.IsReserved(reference)
            ? "A reference of the form '<32 hex digits>:<key>' is reserved for workflow postings."
            : null;
    }

    private static async Task<List<string>> ImmutabilityErrorsAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        var errors = new List<string>();
        var previous = context.Existing!;
        var linesChanged = System.Text.Json.JsonSerializer.Serialize(ContentData.Get(context.Data, "Lines"))
                        != System.Text.Json.JsonSerializer.Serialize(ContentData.Get(previous, "Lines"));
        if (!await WasPostedAsync(context, ct))
            errors.Add("A voided journal entry cannot be edited.");
        else if (linesChanged)
            errors.Add("A posted journal entry cannot be edited. Post a reversing entry instead.");
        if (StatusChanged(context.Data, previous))
            errors.Add("The status of a journal entry changes only by voiding it.");
        return errors;
    }

    private static bool StatusChanged(IReadOnlyDictionary<string, object> data, IReadOnlyDictionary<string, object> previous) =>
        ContentData.AsString(ContentData.Get(data, "Status")) is { Length: > 0 } sent
        && sent != (ContentData.AsString(ContentData.Get(previous, "Status")) ?? string.Empty);

    /// <summary>Whether the stored entry is still posted: neither the older Voided status nor the Void transition has been applied.</summary>
    private static async Task<bool> WasPostedAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        if (ContentData.AsString(ContentData.Get(context.Existing!, "Status")) == "Voided") return false;
        if (context.EntryId is not { } id) return true;
        var stored = await context.Session.LoadAsync<barakoCMS.Models.Content>(id, ct);
        return stored?.LifecycleState != AccountingContentTypes.VoidedState;
    }

    /// <remarks>
    /// The year is the entry's own date's, so an entry dated last December takes last year's next
    /// number. Gapless and in the endpoint's session: the number commits with the entry or not at
    /// all. <c>migrations/5.0.0/accounting-number-sequences.sql</c> carries each year's count over
    /// from the <see cref="NumberSequence"/> rows, so numbering continues where it stopped.
    /// </remarks>
    private async Task<string> NextEntryNumberAsync(
        IDocumentSession session, Dictionary<string, object> data, CancellationToken ct)
    {
        var year = ReadDate(ContentData.Get(data, "Date"))?.Year ?? DateTime.UtcNow.Year;
        var at = new DateTimeOffset(year, 6, 30, 12, 0, 0, TimeSpan.Zero);
        return (await _numbers.NextAsync(session, EntryNumbers, at, ct)).Formatted;
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
