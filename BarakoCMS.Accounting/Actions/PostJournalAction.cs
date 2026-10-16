using System.Globalization;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using BarakoCMS.Accounting.Domain;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Posts the journal entry a named mapping describes for the entry that triggered the workflow.
/// </summary>
/// <remarks>
/// The same subject and <c>ActionKey</c> always post once. The entry takes its id from them, and a
/// retry that finds the entry already there returns it instead of posting again. A mapping line's
/// amount is <c>{{Amount}}</c> (the action's Amount parameter), <c>{{Data.Field}}</c> (a decimal
/// field of the subject) or a decimal literal; anything else fails for good, since waiting will
/// not make it a number.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Post the journal entry a mapping describes for the entry that triggered the workflow",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "Mapping", "ActionKey" },
    OptionalParameters = new[] { "Date", "Memo", "Amount" },
    ExampleJson = @"{""Type"":""Accounting.PostJournal"",""Parameters"":{""Mapping"":""receipt-cash"",""ActionKey"":""receipt""}}"
)]
public sealed class PostJournalAction(LedgerService ledger, AccountMappingStore mappings, IDocumentSession session) : IWorkflowAction
{
    public string Type => "Accounting.PostJournal";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        if (!parameters.TryGetValue("Mapping", out var mappingName) || string.IsNullOrWhiteSpace(mappingName))
            return WorkflowActionResult.PermanentFailure("Parameter 'Mapping' is required.");
        if (!parameters.TryGetValue("ActionKey", out var actionKey) || string.IsNullOrWhiteSpace(actionKey))
            return WorkflowActionResult.PermanentFailure("Parameter 'ActionKey' is required.");
        if (!TryDate(parameters, out var date))
            return WorkflowActionResult.PermanentFailure("Parameter 'Date' is not a date (yyyy-MM-dd).");
        if (await mappings.GetAsync(mappingName, ct) is not { } rule)
            return WorkflowActionResult.PermanentFailure($"No mapping named '{mappingName}'.");

        var reference = PostingReference.For(content.Id, actionKey);
        if (!Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var runId))
            return WorkflowActionResult.PermanentFailure("This action posts only from a workflow run.");

        var posted = await PostingReference.FindAsync(session, reference, ct);
        if (posted.Conflict is not null)
            return WorkflowActionResult.PermanentFailure(posted.Conflict);
        if (posted.Entry is { } existing)
            return Done(existing.Id, ContentData.AsString(ContentData.Get(existing.Data, "EntryNumber")) ?? string.Empty);

        var sensitive = await SensitiveFieldsAsync(content, ct);
        var (lines, problem) = ResolveLines(rule, parameters, content, sensitive);
        problem ??= MemoProblem(parameters.GetValueOrDefault("Memo"), content, sensitive);
        if (problem is not null)
            return WorkflowActionResult.PermanentFailure(problem);
        if (lines.Count == 0)
            return WorkflowActionResult.Success(new Dictionary<string, string> { ["id"] = string.Empty, ["entryNumber"] = string.Empty });

        var command = new PostEntryCommand(date, parameters.GetValueOrDefault("Memo") ?? mappingName, lines, reference);
        var entryId = PostingReference.EntryId(reference);
        var result = await ledger.StageAsync(command, Guid.Empty, new StageOptions(entryId, stored =>
        {
            stored.SourceRunId = runId;
            stored.BranchId = content.BranchId;
        }), ct);

        if (!result.Ok)
            return WorkflowActionResult.PermanentFailure(string.Join(" ", result.Errors));

        await session.SaveChangesAsync(ct);
        return Done(entryId, result.Entry!.EntryNumber);
    }

    /// <summary>The fields of the subject's type that are not Public, which an entry accounting users read must not carry.</summary>
    private async Task<HashSet<string>> SensitiveFieldsAsync(Content subject, CancellationToken ct)
    {
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == subject.ContentType, ct);
        return (definition?.Fields ?? []).Where(f => f.Sensitivity != SensitivityLevel.Public)
            .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Why the memo may not be used: it names a sensitive field, or carries its value. The workflow
    /// runner fills a <c>{{Data.Field}}</c> in a parameter before the action runs, with no regard to
    /// sensitivity, so the value is what shows up here. A short value can match by chance, and that
    /// errs on the side of refusing.
    /// </summary>
    private static string? MemoProblem(string? memo, Content subject, HashSet<string> sensitive)
    {
        if (string.IsNullOrEmpty(memo))
            return null;

        foreach (var field in sensitive)
        {
            var value = ContentData.AsString(ContentData.Get(subject.Data, field));
            if (memo.Contains($"{{{{Data.{field}}}}}", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(value) && memo.Contains(value, StringComparison.OrdinalIgnoreCase)))
                return $"The memo shows the sensitive field '{field}', which an accounting entry must not carry.";
        }

        return null;
    }

    private static WorkflowActionResult Done(Guid id, string number) => WorkflowActionResult.Success(
        new Dictionary<string, string> { ["id"] = id.ToString(), ["entryNumber"] = number },
        [new CreatedRef("journalEntry", id, number)]);

    private static bool TryDate(Dictionary<string, string> parameters, out DateOnly date)
    {
        date = DateOnly.FromDateTime(DateTime.UtcNow);
        return !parameters.TryGetValue("Date", out var text) || string.IsNullOrWhiteSpace(text)
            || DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>The lines to post, with zero lines dropped when the rule says so, or the reason one cannot be read.</summary>
    private static (List<PostLine> Lines, string? Problem) ResolveLines(MappingRule rule, Dictionary<string, string> parameters, Content subject, HashSet<string> sensitive)
    {
        var lines = new List<PostLine>();
        for (var i = 0; i < rule.Lines.Count; i++)
        {
            var line = rule.Lines[i];
            if (!TryAmount(line.Amount, parameters, subject, sensitive, out var amount))
                return ([], $"Line {i + 1}: '{line.Amount}' is not {{{{Amount}}}}, {{{{Data.<Field>}}}} with a number in it, or a decimal.");
            if (rule.SkipIfZero && amount == 0m)
                continue;

            var debit = string.Equals(line.Side, "debit", StringComparison.OrdinalIgnoreCase);
            lines.Add(new PostLine(line.Account, debit ? amount : 0m, debit ? 0m : amount));
        }

        return (lines, null);
    }

    private static bool TryAmount(string text, Dictionary<string, string> parameters, Content subject, HashSet<string> sensitive, out decimal amount)
    {
        amount = 0m;
        var trimmed = text.Trim();
        if (trimmed == "{{Amount}}")
            return parameters.TryGetValue("Amount", out var given) && TryDecimal(given, out amount);

        if (trimmed.StartsWith("{{Data.", StringComparison.Ordinal) && trimmed.EndsWith("}}", StringComparison.Ordinal))
        {
            var field = trimmed[7..^2];
            return !sensitive.Contains(field)
                && ContentData.Get(subject.Data, field) is { } value && TryDecimal(Convert.ToString(value, CultureInfo.InvariantCulture), out amount);
        }

        return TryDecimal(trimmed, out amount);
    }

    private static bool TryDecimal(string? text, out decimal amount) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
}
