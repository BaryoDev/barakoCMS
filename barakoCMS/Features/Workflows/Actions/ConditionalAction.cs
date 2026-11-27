using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for conditional logic (if/then/else).
/// Evaluates a condition and executes different action sets based on the result.
/// </summary>
[WorkflowActionMetadata(
    Description = "Conditional if/then/else logic for workflows",
    Group = WorkflowActionGroup.Flow,
    RequiredParameters = new[] { "Condition", "ThenActions" },
    OptionalParameters = new[] { "ElseActions" },
    ExampleJson = @"{""Type"":""Conditional"",""Parameters"":{""Condition"":""{{status}} == Published"",""ThenActions"":""[{\""Type\"":\""Email\"",\""Parameters\"":{\""To\"":\""admin@example.com\""}}]""}}"
)]
internal class ConditionalAction : IWorkflowAction
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ConditionalAction> _logger;

    /// <summary>
    /// Creates a new ConditionalAction.
    /// </summary>
    public ConditionalAction(IServiceProvider serviceProvider, ILogger<ConditionalAction> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "Conditional";

    /// <summary>
    /// Only here because the interface still declares it. <see cref="RunAsync"/> is the contract
    /// this action implements, and delegating keeps a caller on the older path behaving the same.
    /// </summary>
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Before this (#572), a failing child was logged and dropped: the conditional reported success
    /// even when a branch did nothing it was configured to do. Every child now feeds into the result
    /// this returns, so a swallowed failure cannot happen again.
    ///
    /// Children still run inline with no attempt record and no idempotency key of their own (that is
    /// the reshape #572 defers to 4.1), which bounds what "retryable" can safely mean here: retrying
    /// this action re-runs every child from the top, including ones that already had their effect. So
    /// a retry is only offered when nothing has succeeded yet; the moment one child has succeeded
    /// alongside a failing one, this reports <see cref="WorkflowActionResult.PermanentFailure"/> so the
    /// runner does not resend what the earlier child already sent.
    /// </remarks>
    public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        RunBranchAsync(parameters, content, NoOutputs, ct);

    /// <summary>The runner's entry, so the condition can read what the steps before it kept as <c>actions.N.name</c>.</summary>
    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        RunBranchAsync(context.Parameters, context.Content, context.Outputs, ct);

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> NoOutputs =
        new Dictionary<int, IReadOnlyDictionary<string, string>>();

    private async Task<WorkflowActionResult> RunBranchAsync(
        Dictionary<string, string> parameters,
        barakoCMS.Models.Content content,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs,
        CancellationToken ct)
    {
        var condition = parameters.GetValueOrDefault("Condition");
        var thenActionsJson = parameters.GetValueOrDefault("ThenActions");
        var elseActionsJson = parameters.GetValueOrDefault("ElseActions");

        if (string.IsNullOrEmpty(condition))
        {
            return WorkflowActionResult.PermanentFailure("Conditional action is missing its 'Condition' parameter.");
        }

        if (content is ErasedContent && ConditionExpression.ReadsTheEntry(condition))
        {
            return WorkflowActionResult.PermanentFailure(
                "The entry was erased, so a condition on its status or data cannot be evaluated.");
        }

        var verdict = await EvaluateConditionAsync(condition, content, outputs, ct);
        var result = await RunSelectedAsync(
            parameters, content, verdict.Result, verdict.Result ? thenActionsJson : elseActionsJson, ct);

        // The note says why a condition that could not be evaluated is false. It holds no value.
        return verdict.Note is null ? result : result with { Note = verdict.Note };
    }

    private async Task<WorkflowActionResult> RunSelectedAsync(
        Dictionary<string, string> parameters,
        barakoCMS.Models.Content content,
        bool conditionResult,
        string? actionsToExecute,
        CancellationToken ct)
    {

        if (string.IsNullOrEmpty(actionsToExecute))
        {
            _logger.LogInformation(
                "Conditional evaluated to {Result} but no actions defined for that branch",
                conditionResult);
            return WorkflowActionResult.Success();
        }

        List<ChildAction>? actions;
        try
        {
            actions = JsonSerializer.Deserialize<List<ChildAction>>(actionsToExecute);
        }
        catch (JsonException)
        {
            // A malformed action list is a configuration problem, not a transient one: the JSON does
            // not become valid on the next attempt.
            return WorkflowActionResult.PermanentFailure(
                $"The '{(conditionResult ? "Then" : "Else")}Actions' parameter is not valid JSON.");
        }

        // Saving and reading skip a branch they cannot read the same way this does, so its
        // credentials were never encrypted. Running it would use them as stored. Before the empty
        // check, since the literal null deserialises to no list at all and is not a readable branch.
        if (!WebhookSigning.IsReadableBranch(actionsToExecute))
        {
            return WorkflowActionResult.PermanentFailure(
                WebhookSigning.UnreadableBranchReason(conditionResult ? "ThenActions" : "ElseActions"));
        }

        if (actions == null || actions.Count == 0)
        {
            return WorkflowActionResult.Success();
        }

        await using var availableActions = WorkflowActionSet.Build(_serviceProvider, _logger);
        if (availableActions.Actions == null)
        {
            return WorkflowActionResult.PermanentFailure("No workflow actions are registered, so the conditional's children could not run.");
        }

        var tally = new Tally();
        foreach (var childAction in actions)
        {
            await RunChildAsync(childAction, availableActions, parameters, content, tally, ct);
        }

        _logger.LogInformation(
            "Conditional action executed {Branch} branch with {Count} actions, {Failed} failed",
            conditionResult ? "then" : "else", actions.Count, tally.FailedTypes.Count);

        return Summarise(tally, conditionResult ? "then" : "else", actions.Count);
    }

    /// <summary>What the children of one branch did so far.</summary>
    private sealed class Tally
    {
        public int Succeeded;
        public readonly List<string> FailedTypes = [];
        public readonly List<string> CredentialErrors = [];
        public bool AnyPermanentFailure;
    }

    private async Task RunChildAsync(
        ChildAction childAction,
        WorkflowActionSet availableActions,
        Dictionary<string, string> parameters,
        barakoCMS.Models.Content content,
        Tally tally,
        CancellationToken ct)
    {
        var plugin = availableActions.Find(childAction.Type);
        if (plugin == null && availableActions.NotBuiltError(childAction.Type) is not null)
        {
            // Not permanent: the child that could not be built may be this one, and the runner's
            // attempt limit still ends the retries.
            _logger.LogWarning("Action type {Type} not found while some actions could not be built", childAction.Type);
            tally.FailedTypes.Add(childAction.Type);
            return;
        }

        if (plugin == null)
        {
            _logger.LogWarning("Action type {Type} not found", childAction.Type);
            tally.FailedTypes.Add(childAction.Type);
            tally.AnyPermanentFailure = true;
            return;
        }

        // The registered extractor when there is one, so children resolve exactly as top-level
        // actions do. A host that registers none still gets the built-in rules.
        var extractor = _serviceProvider.GetService<ITemplateVariableExtractor>();

        var childParameters = Unprotect(childAction, tally);
        if (childParameters is null)
        {
            return;
        }

        WorkflowActionResult childResult;
        try
        {
            childResult = await plugin.RunAsync(
                ActionParameters.WithTriggerOf(parameters, ResolveChild(extractor, childAction.Type, childParameters, content)),
                content, ct);
        }
        catch (Exception ex) when (ex is SendOutcomeUnknownException || (ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            throw;
        }
        catch (Exception ex)
        {
            // The child's own exception may carry whatever it was sending (a recipient, a
            // payload), so only its type is kept here. WorkflowRunner.cs stores the exception
            // message too (see #598); this is deliberately stricter, not aligned with that, since
            // this string is stored on the run record and served over the API.
            _logger.LogWarning(ex, "Child action {Type} threw", childAction.Type);
            tally.FailedTypes.Add(childAction.Type);
            return;
        }

        Record(childResult, childAction.Type, tally);
    }

    private void Record(WorkflowActionResult childResult, string type, Tally tally)
    {
        if (childResult.Succeeded)
        {
            tally.Succeeded++;
            return;
        }

        // The child's error is dropped, not relocated: it can hold what the child was trying to
        // send, and neither the run record nor the log is a safe home for text a child action
        // composed. A log aggregator is a different place, not a safer one, same reasoning
        // WebhookAction applies to an HttpRequestException's message.
        _logger.LogWarning("Child action {Type} failed", type);
        tally.FailedTypes.Add(type);
        if (!childResult.Retryable)
        {
            tally.AnyPermanentFailure = true;
        }
    }

    /// <summary>
    /// The same decryption the runner does for a top-level action: every credential but Secret, which
    /// Webhook decrypts itself. A nested Conditional decrypts its own children. Null when a
    /// credential cannot be read, with the failure on the tally.
    /// </summary>
    private Dictionary<string, string>? Unprotect(ChildAction childAction, Tally tally)
    {
        var protector = _serviceProvider.GetService<ISecretProtector>();
        if (protector is null)
        {
            return childAction.Parameters;
        }

        var (unprotected, credentialError) = WebhookSigning.UnprotectCredentials(childAction.Parameters, protector);
        if (credentialError is null)
        {
            return unprotected;
        }

        // The error names the parameter and never holds its value, so unlike a child's
        // own error it is safe on the run record. Permanent: a retry has the same key.
        tally.CredentialErrors.Add($"{childAction.Type}: {credentialError}");
        tally.FailedTypes.Add(childAction.Type);
        tally.AnyPermanentFailure = true;
        return null;
    }

    private static WorkflowActionResult Summarise(Tally tally, string branch, int count)
    {
        if (tally.FailedTypes.Count == 0)
        {
            return WorkflowActionResult.Success();
        }

        var names = string.Join(", ", tally.FailedTypes.Distinct());
        var message = tally.FailedTypes.Count == count
            ? $"All {count} child action(s) failed in the {branch} branch: {names}."
            : $"{tally.FailedTypes.Count} of {count} child action(s) failed in the {branch} branch: {names}.";

        if (tally.CredentialErrors.Count > 0)
        {
            message += " " + string.Join(" ", tally.CredentialErrors);
        }

        // A retry re-runs every child from the top, so it is only safe when nothing has succeeded
        // yet. Once one child has succeeded alongside a failure, retrying would resend what that
        // child already sent, which is its own defect (see the remarks above).
        if (tally.Succeeded > 0 || tally.AnyPermanentFailure)
        {
            return WorkflowActionResult.PermanentFailure(message);
        }

        return WorkflowActionResult.Failure(message);
    }

    /// <summary>
    /// A child's parameters with their placeholders filled, by the host's extractor when it registers
    /// one, and with the parameters the child's plugin fills in itself left alone.
    /// </summary>
    private Dictionary<string, string> ResolveChild(
        ITemplateVariableExtractor? extractor, string type, Dictionary<string, string> childParameters, barakoCMS.Models.Content content)
    {
        var registry = _serviceProvider.GetService<IWorkflowPluginRegistry>();
        return extractor is null
            ? ActionParameters.Resolve(type, childParameters, content, registry)
            : ActionParameters.Resolve(extractor, type, childParameters, content, registry);
    }

    private async Task<ConditionExpression.Verdict> EvaluateConditionAsync(
        string condition,
        barakoCMS.Models.Content content,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs,
        CancellationToken ct)
    {
        var prepared = ConditionExpression.Prepare(condition, content, outputs);
        var zone = prepared.UsesClock && _serviceProvider.GetService<Marten.IQuerySession>() is { } session
            ? await TemplateVariableExtractor.TenantZoneAsync(session, ct)
            : null;

        var verdict = ConditionExpression.Run(prepared, zone);
        if (verdict.Note is not null)
        {
            _logger.LogWarning("A condition could not be evaluated, so it is false: {Note}", verdict.Note);
        }

        return verdict;
    }

    private class ChildAction
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
