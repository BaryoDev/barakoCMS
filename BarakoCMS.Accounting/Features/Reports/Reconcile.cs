using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Marten.Linq.MatchesSql;

namespace BarakoCMS.Accounting.Features.Reports;

/// <summary>One subject entry the books do not agree with.</summary>
[Obsolete("BarakoCMS.Accounting is deprecated. Removal planned for barakoCMS 6.0.")]
public sealed record ReconcileRow(Guid SubjectId, string? SubjectNumber, decimal SubjectAmount, decimal JournalAmount, string Problem);

/// <summary>How many subjects were checked, and the ones that differ or have no posting (at most 500 of each).</summary>
[Obsolete("BarakoCMS.Accounting is deprecated. Removal planned for barakoCMS 6.0.")]
public sealed record ReconcileResponse(int Checked, List<ReconcileRow> Mismatches, List<ReconcileRow> Missing);

/// <summary>
/// GET /api/accounting/reconcile?contentType=&amp;from=&amp;to=&amp;actionKey= compares each entry of a
/// content type made in the range with what the workflow posted for it.
/// </summary>
/// <remarks>
/// A subject's amount is its <c>Total</c> field, or the sum of the fields <c>amountFields</c> names
/// (comma separated, at most 8 after blanks and repeats are dropped, more is a 400). Each name must be a field the
/// type declares (matched without regard to case and read under its declared name), or the answer is a 400,
/// as it is when the type cannot be found. Without <c>amountFields</c> a missing <c>Total</c> counts as zero. It matches when the entries a workflow run posted for
/// it add up to that amount, or when the posting was reversed (a reversed posting is settled, however
/// large the subject still reads, and only the key that was reversed). Only entries a workflow run made count, as when the action looks
/// for its own posting. <c>Number</c> and every amount field are read only when the type marks them Public,
/// since this route is open to anyone who may view the ledger and not to anyone who may read the
/// subject. <c>from</c> and <c>to</c> are dates, both days included, at most 366 days apart.
/// </remarks>
[Obsolete("BarakoCMS.Accounting is deprecated. Removal planned for barakoCMS 6.0.")]
public class ReconcileEndpoint(IQuerySession session, IPermissionResolver permissions) : Endpoint<ReconcileEndpoint.Request, ReconcileResponse>
{
    private const int BatchSize = 200;
    private const int ListCap = 500;
    private const int MaxDays = 366;
    private const int MaxAmountFields = 8;

    public class Request
    {
        public string ContentType { get; set; } = string.Empty;
        public DateOnly? From { get; set; }
        public DateOnly? To { get; set; }
        public string? ActionKey { get; set; }
        public string? AmountFields { get; set; }
    }

    public override void Configure()
    {
        Get("/api/accounting/reconcile");
        Definition.RequireCapability(
            AccountingCapabilities.ViewLedger, AccountingCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ContentType))
            AddError("contentType is required.");
        if (req.From is not { } from || req.To is not { } to)
            AddError("from and to are required.");
        else if (to < from || to.DayNumber - from.DayNumber + 1 > MaxDays)
            AddError($"The range must run forward and cover at most {MaxDays} days.");
        var amountFields = ParseAmountFields(req.AmountFields);
        var named = req.AmountFields?.Split(',').Any(n => !string.IsNullOrWhiteSpace(n)) == true;
        if (amountFields.Count > MaxAmountFields)
        {
            AddError($"amountFields names {amountFields.Count} fields, at most {MaxAmountFields} are allowed.");
        }
        if (ValidationFailures.Count == 0 && named)
        {
            var declared = await DeclaredAsync(req.ContentType, amountFields, ct);
            if (declared.Error is { } error)
            {
                AddError(error);
            }
            else
            {
                amountFields = declared.Names;
            }
        }
        if (ValidationFailures.Count == 0 && await NotPublicAsync(req.ContentType, amountFields, ct) is { } field)
            AddError($"'{field}' is not a public field of '{req.ContentType}', so it cannot be reconciled.");
        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        await Send.ResponseAsync(await ReconcileAsync(req, user, amountFields, ct), cancellation: ct);
    }

    /// <summary>The named fields, trimmed, without blanks or repeats (names match without regard to case); <c>Total</c> when none are named.</summary>
    private static List<string> ParseAmountFields(string? value)
    {
        var names = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count == 0 ? ["Total"] : names;
    }

    private async Task<(List<string> Names, string? Error)> DeclaredAsync(string contentType, List<string> names, CancellationToken ct)
    {
        var definition = await AccountContentTypes.ResolveAsync(session, contentType, ct);
        if (definition is null)
        {
            return ([], $"'{contentType}' was not found, so its amount fields cannot be checked.");
        }
        var declared = new List<string>();
        foreach (var name in names)
        {
            var match = definition.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return ([], $"'{name}' is not a field of '{contentType}', so it cannot be reconciled.");
            }
            declared.Add(match.Name);
        }

        return (declared.Distinct(StringComparer.Ordinal).ToList(), null);
    }

    private async Task<string?> NotPublicAsync(string contentType, List<string> amountFields, CancellationToken ct)
    {
        var definition = await AccountContentTypes.ResolveAsync(session, contentType, ct);
        return definition?.Fields.FirstOrDefault(f => f.Sensitivity != SensitivityLevel.Public
            && (string.Equals(f.Name, "Number", StringComparison.OrdinalIgnoreCase)
                || amountFields.Contains(f.Name, StringComparer.OrdinalIgnoreCase)))?.Name;
    }

    private async Task<ReconcileResponse> ReconcileAsync(Request req, User user, List<string> amountFields, CancellationToken ct)
    {
        var start = req.From!.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = req.To!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var response = new ReconcileResponse(0, [], []);
        var checkedCount = 0;
        var branchFilter = await Resolve<IBranchScopeGuard>().FilterAsync(User, ct);

        for (var page = 0; ; page++)
        {
            var rows = session.Query<Content>()
                .Where(c => c.ContentType == req.ContentType && c.CreatedAt >= start && c.CreatedAt < end);
            if (branchFilter is not null)
                rows = rows.Where(c => c.MatchesSql(branchFilter.Sql, branchFilter.Parameters));

            var batch = await rows
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Skip(page * BatchSize).Take(BatchSize)
                .ToListAsync(ct);
            if (batch.Count == 0)
                break;

            var subjects = new List<Content>();
            foreach (var row in batch)
            {
                if (await permissions.CanPerformActionAsync(user, row.ContentType, "read", row, ct))
                    subjects.Add(row);
            }

            var postings = subjects.Count == 0 ? [] : await PostingsAsync(subjects, ct);
            foreach (var subject in subjects)
            {
                checkedCount++;
                Compare(subject, postings.GetValueOrDefault(subject.Id) ?? [], req.ActionKey, amountFields, response);
            }

            if (batch.Count < BatchSize)
                break;
        }

        return response with { Checked = checkedCount };
    }

    /// <summary>The entries a workflow run posted for each subject of the batch, by subject id, from one query.</summary>
    private async Task<Dictionary<Guid, List<Content>>> PostingsAsync(IReadOnlyList<Content> subjects, CancellationToken ct)
    {
        var prefixes = subjects.Select(s => $"{s.Id:N}:%").ToArray();
        // One array parameter: passed bare, a string[] would spread over params object[] as one parameter per prefix.
        var entries = await session.Query<Content>()
            .Where(c => c.ContentType == AccountingContentTypes.JournalEntry)
            .Where(c => c.MatchesSql("d.data -> 'Data' ->> 'Reference' LIKE ANY(?)", new object[] { prefixes }))
            .ToListAsync(ct);

        var byId = subjects.ToDictionary(s => $"{s.Id:N}", s => s.Id);
        var result = new Dictionary<Guid, List<Content>>();
        foreach (var entry in entries.Where(e => e.SourceRunId is not null))
        {
            var reference = ContentData.AsString(ContentData.Get(entry.Data, "Reference")) ?? string.Empty;
            if (reference.Length > 33 && byId.TryGetValue(reference[..32], out var subjectId))
                (result.TryGetValue(subjectId, out var list) ? list : result[subjectId] = []).Add(entry);
        }

        return result;
    }

    private static void Compare(Content subject, IReadOnlyList<Content> entries, string? actionKey, List<string> amountFields, ReconcileResponse response)
    {
        string KeyOf(Content e) => (ContentData.AsString(ContentData.Get(e.Data, "Reference")) ?? string.Empty)[33..];
        IReadOnlyList<Content> mine = string.IsNullOrWhiteSpace(actionKey)
            ? entries
            : entries.Where(e => KeyOf(e) == actionKey || KeyOf(e) == actionKey + ":reverse").ToList();

        // A reversed key is settled, and only that key: the entries of the others still have to add up.
        const string Reversed = ":reverse";
        var reversedKeys = mine.Select(KeyOf).Where(k => k.EndsWith(Reversed, StringComparison.Ordinal))
            .Select(k => k[..^Reversed.Length]).ToHashSet(StringComparer.Ordinal);
        if (reversedKeys.Count > 0)
        {
            mine = mine.Where(e => !reversedKeys.Contains(KeyOf(e).EndsWith(Reversed, StringComparison.Ordinal) ? KeyOf(e)[..^Reversed.Length] : KeyOf(e))).ToList();
            if (mine.Count == 0)
                return;
        }

        var live = mine.Where(e => e.LifecycleState != AccountingContentTypes.VoidedState).ToList();
        var subjectAmount = amountFields.Sum(f => ContentData.AsDecimal(ContentData.Get(subject.Data, f)));
        var number = ContentData.AsString(ContentData.Get(subject.Data, "Number"));
        if (live.Count == 0)
        {
            Add(response.Missing, new ReconcileRow(subject.Id, number, subjectAmount, 0m, "No posting."));
            return;
        }

        var journalAmount = live.Sum(DebitTotal);
        if (journalAmount != subjectAmount)
            Add(response.Mismatches, new ReconcileRow(subject.Id, number, subjectAmount, journalAmount, "Amount differs."));
    }

    /// <summary>What an entry moves: the sum of its debit lines, which equals its credits.</summary>
    private static decimal DebitTotal(Content entry) =>
        ((System.Collections.IEnumerable?)ContentData.Get(entry.Data, "Lines") ?? Array.Empty<object>())
        .OfType<IDictionary<string, object>>()
        .Sum(l => ContentData.AsDecimal(l.TryGetValue("Debit", out var d) ? d : null));

    private static void Add(List<ReconcileRow> rows, ReconcileRow row)
    {
        if (rows.Count < ListCap)
            rows.Add(row);
    }
}
