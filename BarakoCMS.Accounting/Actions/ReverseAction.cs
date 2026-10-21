using System.Globalization;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Reverses the entry <c>Accounting.PostJournal</c> posted for a subject and key: posts the same
/// lines with debits and credits swapped, then voids the original.
/// </summary>
/// <remarks>
/// A retry finishes the job rather than repeating it. The reversal takes its id from the original's
/// reference, so it is posted once, and a retry that finds it still voids an original left posted by
/// a crash between the two steps. Voiding takes the original and its reversal out of the books
/// together, so the balances come back to what they were before the posting.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Reverse the journal entry posted for an entry and key, and void the original",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "ActionKey", "Reason" },
    OptionalParameters = new[] { "Target", "Date" },
    ExampleJson = @"{""Type"":""Accounting.Reverse"",""Parameters"":{""ActionKey"":""receipt"",""Reason"":""Receipt cancelled""}}"
)]
public sealed class ReverseAction(LedgerService ledger, IContentTransitioner transitioner, IDocumentSession session) : IWorkflowAction
{
    public string Type => "Accounting.Reverse";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        if (!parameters.TryGetValue("ActionKey", out var actionKey) || string.IsNullOrWhiteSpace(actionKey))
            return WorkflowActionResult.PermanentFailure("Parameter 'ActionKey' is required.");
        if (!parameters.TryGetValue("Reason", out var reason) || string.IsNullOrWhiteSpace(reason))
            return WorkflowActionResult.PermanentFailure("Parameter 'Reason' is required.");

        var target = parameters.TryGetValue("Target", out var text) && !string.IsNullOrWhiteSpace(text)
            ? (Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null)
            : content.Id;
        if (target is not { } subjectId)
            return WorkflowActionResult.PermanentFailure("Parameter 'Target' is not an id.");

        if (!Guid.TryParse(parameters.GetValueOrDefault("RunId"), out _))
            return WorkflowActionResult.PermanentFailure("This action reverses only from a workflow run.");

        var sensitive = await SubjectSensitivity.FieldsAsync(session, content, ct);
        if (SubjectSensitivity.Problem([actionKey, reason, text], content, sensitive) is { } shown)
            return WorkflowActionResult.PermanentFailure(shown);

        var originalLookup = await PostingReference.FindAsync(session, PostingReference.For(subjectId, actionKey), ct);
        var reference = PostingReference.For(subjectId, actionKey) + ":reverse";
        var reversalLookup = await PostingReference.FindAsync(session, reference, ct);
        if ((originalLookup.Conflict ?? reversalLookup.Conflict) is { } conflict)
            return WorkflowActionResult.PermanentFailure(conflict);

        var (original, reversal) = (originalLookup.Entry, reversalLookup.Entry);
        if (original is null || (reversal is null && original.LifecycleState == AccountingContentTypes.VoidedState))
            return WorkflowActionResult.PermanentFailure("No posting to reverse.");

        var reversalId = PostingReference.EntryId(reference);
        var number = reversal is null ? string.Empty : ContentData.AsString(ContentData.Get(reversal.Data, "EntryNumber")) ?? string.Empty;
        if (original is not null && !string.IsNullOrWhiteSpace(ContentData.AsString(ContentData.Get(original.Data, "VoidsEntryId"))))
            return WorkflowActionResult.PermanentFailure("That entry is itself a reversal, which cannot be reversed. Post the original again instead.");

        if (reversal is null)
        {
            var posted = await PostReversalAsync(original, reference, reversalId, parameters, ct);
            if (!posted.Ok)
                return WorkflowActionResult.PermanentFailure(string.Join(" ", posted.Errors));
            number = posted.Entry!.EntryNumber;
            await session.SaveChangesAsync(ct);
        }

        if (await VoidAsync(original, reason, ct) is { } error)
            return WorkflowActionResult.Failure(error);

        return WorkflowActionResult.Success(
            new Dictionary<string, string> { ["id"] = reversalId.ToString(), ["entryNumber"] = number },
            [new CreatedRef("journalEntry", reversalId, number)]);
    }

    private Task<PostResult> PostReversalAsync(
        Content original, string reference, Guid reversalId, Dictionary<string, string> parameters, CancellationToken ct)
    {
        var lines = ((System.Collections.IEnumerable)(ContentData.Get(original.Data, "Lines") ?? Array.Empty<object>()))
            .OfType<IDictionary<string, object>>()
            .Select(l => l.AsReadOnly())
            .Select(l => new PostLine(
                ContentData.AsString(ContentData.Get(l, "AccountCode")) ?? string.Empty,
                ContentData.AsDecimal(ContentData.Get(l, "Credit")),
                ContentData.AsDecimal(ContentData.Get(l, "Debit")),
                ContentData.AsString(ContentData.Get(l, "Memo"))))
            .ToList();

        var date = parameters.TryGetValue("Date", out var text)
            && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var given)
                ? given
                : DateOnly.FromDateTime(DateTime.UtcNow);
        var number = ContentData.AsString(ContentData.Get(original.Data, "EntryNumber"));
        _ = Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var runId);

        return ledger.StageAsync(
            new PostEntryCommand(date, $"Reversal of {number}", lines, reference, original.Id), Guid.Empty,
            new StageOptions(reversalId, stored =>
            {
                stored.SourceRunId = runId;
                stored.BranchId = original.BranchId;
            }), ct);
    }

    /// <summary>Voids the original, or returns why it could not. An original that is already voided is done.</summary>
    private async Task<string?> VoidAsync(Content original, string reason, CancellationToken ct)
    {
        if (original.LifecycleState == AccountingContentTypes.VoidedState)
            return null;

        var result = await transitioner.TransitionAsync(
            original, "Void", ContentTransitionActor.ForSystem("accounting"),
            new ContentTransitionOptions { SkipPermissionChecks = true, Data = new Dictionary<string, object> { ["VoidReason"] = reason } }, ct);
        if (result.Succeeded)
            return null;

        // A second run reaches a Conflict here, not Invalid: the entry is no longer in the state the
        // move starts from. Reading it again tells that apart from a move that really failed.
        var current = await session.LoadAsync<Content>(original.Id, ct);
        return current?.LifecycleState == AccountingContentTypes.VoidedState
            ? null
            : string.Join(" ", result.Errors);
    }
}
