using System.Globalization;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Services;
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
///
/// Mapping, ActionKey, Memo and Amount arrive as written (see <c>SelfResolvedParameters</c>). A
/// placeholder in one that reads a field of the subject that is not Public fails for good, by the
/// field's name; the rest is filled in by the action the way the runner fills any parameter.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Post the journal entry a mapping describes for the entry that triggered the workflow",
    Group = WorkflowActionGroup.Content,
    RequiredCapability = AccountingCapabilities.PostEntries,
    RequiredParameters = new[] { "Mapping", "ActionKey" },
    OptionalParameters = new[] { "Date", "Memo", "Amount" },
    SelfResolvedParameters = new[] { "Mapping", "ActionKey", "Memo", "Amount" },
    ExampleJson = @"{""Type"":""Accounting.PostJournal"",""Parameters"":{""Mapping"":""receipt-cash"",""ActionKey"":""receipt""}}"
)]
[Obsolete("BarakoCMS.Accounting is deprecated and is removed in a later release.")]
public sealed class PostJournalAction(LedgerService ledger, AccountMappingStore mappings, IDocumentSession session, ITemplateVariableExtractor? extractor = null) : IWorkflowAction
{
    public string Type => "Accounting.PostJournal";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        await PostAsync(parameters, content, ct);

    private async Task<WorkflowActionResult> PostAsync(
        Dictionary<string, string> raw, Content content, CancellationToken ct)
    {
        // Mapping, ActionKey, Memo and Amount arrive as written: the check is by the name of the
        // field a placeholder reads, which a filled-in value no longer tells.
        var sensitive = await SubjectSensitivity.FieldsAsync(session, content, ct);
        if (SubjectSensitivity.Fill(raw, SubjectSensitivity.PostJournalParameters, content, sensitive, extractor, out var problem)
            is not { } parameters)
            return WorkflowActionResult.PermanentFailure(problem!);

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

        var chart = (await AccountingContentReader.AccountsAsync(session, ct))
            .GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Code, StringComparer.OrdinalIgnoreCase);
        var (lines, linesProblem) = ResolveLines(rule, parameters, content, sensitive, chart);
        if (linesProblem is not null)
            return WorkflowActionResult.PermanentFailure(linesProblem);
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
    private static (List<PostLine> Lines, string? Problem) ResolveLines(MappingRule rule, Dictionary<string, string> parameters, Content subject, HashSet<string> sensitive, Dictionary<string, string> chart)
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
            lines.Add(new PostLine(chart.GetValueOrDefault(line.Account, line.Account), debit ? amount : 0m, debit ? 0m : amount));
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
