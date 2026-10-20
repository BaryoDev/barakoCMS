#pragma warning disable CS0618 // needs the write session itself (locks, number allocation, reads of definitions), which IContentHookContext does not offer yet
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
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
/// Posts balanced double-entry journal entries.
///
/// Journal entries are content now (the project's content-type-first rule), so this service no
/// longer owns either the storage or the rules: it builds the entry's data bag and runs it through
/// <see cref="JournalEntryHook"/> — the very same hook the generic <c>/api/contents</c> endpoint
/// runs — then stores it as content. That keeps one source of truth for the balance invariant and
/// the entry numbering, and means a post through this service and a post through the generic
/// endpoint cannot diverge.
///
/// It exists at all only so the module's existing <c>POST /api/accounting/journal-entries</c> keeps
/// working for consumers already calling it. New callers should prefer the generic
/// content endpoint.
/// </summary>
public class LedgerService
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly JournalEntryHook _hook;

    /// <summary>
    /// The single-argument form this replaces.
    /// </summary>
    /// <remarks>
    /// Kept because BarakoCMS.Accounting ships as a package and this is a plain service an external
    /// caller can construct. It builds the same writer the container would, from the same session,
    /// so behaviour is identical.
    ///
    /// The endpoints changed alongside it are not given this treatment on purpose: FastEndpoints
    /// constructs those, and nothing outside the process news one up.
    /// </remarks>
    [Obsolete("Use the constructor taking IContentWriter. Removal planned for barakoCMS 6.0.")]
    public LedgerService(IDocumentSession session)
        : this(session, new barakoCMS.Infrastructure.Services.ContentWriter(
            session, new barakoCMS.Infrastructure.Services.ContentSourcingPolicyService(session)))
    {
    }

    /// <summary>The form before 5.0, kept for an external caller that constructs the service.</summary>
    /// <remarks>
    /// Numbers through the core gapless allocator, as the content endpoint does, so posts made here
    /// and there share one count.
    /// </remarks>
    [Obsolete("Use the constructor taking INumberAllocator. Removal planned for barakoCMS 6.0.")]
    public LedgerService(IDocumentSession session, IContentWriter contentWriter)
        : this(session, contentWriter, barakoCMS.Infrastructure.Numbering.NumberAllocators.Gapless())
    {
    }

    public LedgerService(IDocumentSession session, IContentWriter contentWriter, barakoCMS.Core.Interfaces.INumberAllocator numbers)
    {
        _session = session;
        _contentWriter = contentWriter;
        _hook = new JournalEntryHook(numbers);
    }

    /// <summary>
    /// Posts the entry and saves, in one transaction. Use <see cref="StageAsync"/> instead when the
    /// caller has more to commit with it.
    /// </summary>
    public async Task<PostResult> PostAsync(PostEntryCommand cmd, Guid userId, CancellationToken ct)
    {
        var result = await StageAsync(cmd, userId, ct);
        if (result.Ok)
        {
            await _session.SaveChangesAsync(ct);
        }
        return result;
    }

    /// <summary>
    /// Does everything <see cref="PostAsync"/> does except save: the same validation, the same entry
    /// number, the content and its event stream on the shared session. The caller's own
    /// <c>SaveChangesAsync</c> commits the entry, the number sequence and whatever else the caller
    /// staged, in one transaction. A rejected entry stages nothing and returns its errors.
    /// </summary>
    public async Task<PostResult> StageAsync(PostEntryCommand cmd, Guid userId, CancellationToken ct)
    {
        var data = ToContentData(cmd);

        // The hook both validates and stamps EntryNumber/Amount. A rejected post never reaches the
        // store and never consumes a sequence number.
        var errors = await _hook.OnBeforeSaveAsync(
            new barakoCMS.Core.Interfaces.ContentLifecycleContext
            {
                ContentType = AccountingContentTypes.JournalEntry,
                Data = data,
                Existing = null,
                Session = _session,
                UserId = userId,
            }, ct);

        if (errors.Count > 0)
        {
            return new PostResult(null, errors.ToList());
        }

        // Start the event stream as well as storing the read model — exactly what the generic
        // content endpoint does. Without this an entry posted through this route would have no
        // history, and traceability is the entire point of a ledger.
        var contentId = Guid.NewGuid();
        var searchText = await SearchTextAsync(data, ct);
        // Sensitivity stated rather than defaulted. A journal entry is Public, but the six-value
        // constructor supplied that silently and this is the field a rebuild must not have to guess.
        var created = new barakoCMS.Events.ContentCreated(
            contentId, AccountingContentTypes.JournalEntry, data,
            barakoCMS.Models.ContentStatus.Published, userId, searchText,
            barakoCMS.Models.SensitivityLevel.Public);

        await _contentWriter.CreateAsync(created, ct);

        return PostResult.Success(new JournalEntry
        {
            Id = contentId,
            EntryNumber = ContentData.AsString(ContentData.Get(data, "EntryNumber")) ?? string.Empty,
            Date = cmd.Date,
            Memo = cmd.Memo,
            Reference = cmd.Reference,
            VoidsEntryId = cmd.VoidsEntryId,
            Status = JournalStatus.Posted,
            Amount = ContentData.AsDecimal(ContentData.Get(data, "Amount")),
            Attachments = (cmd.Attachments ?? Array.Empty<string>()).ToList(),
            CreatedBy = userId,
            Lines = cmd.Lines
                .Select(l => new JournalLine { AccountCode = l.AccountCode, Debit = l.Debit, Credit = l.Credit, Memo = l.Memo })
                .ToList(),
        });
    }

    private static Dictionary<string, object> ToContentData(PostEntryCommand cmd) => new()
    {
        ["Date"] = cmd.Date.ToString("yyyy-MM-dd"),
        ["Memo"] = cmd.Memo,
        ["Reference"] = cmd.Reference ?? string.Empty,
        ["Status"] = JournalStatus.Posted.ToString(),
        ["VoidsEntryId"] = cmd.VoidsEntryId?.ToString() ?? string.Empty,
        ["Attachments"] = (cmd.Attachments ?? Array.Empty<string>()).Cast<object>().ToList(),
        ["Lines"] = cmd.Lines
            .Select(l => (object)new Dictionary<string, object>
            {
                ["AccountCode"] = l.AccountCode,
                ["Debit"] = l.Debit,
                ["Credit"] = l.Credit,
                ["Memo"] = l.Memo ?? string.Empty,
            })
            .ToList(),
    };

    // The search text holds only the fields the journal entry type marks Public.
    private async Task<string> SearchTextAsync(Dictionary<string, object> data, CancellationToken ct)
    {
        var definition = await _session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == AccountingContentTypes.JournalEntry, ct);

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return string.Join(
            ' ',
            data
                .Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));
    }
}
