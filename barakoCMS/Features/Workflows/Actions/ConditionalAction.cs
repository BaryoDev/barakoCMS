using barakoCMS.Infrastructure.Attributes;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for conditional logic (if/then/else).
/// Evaluates a condition and executes different action sets based on the result.
/// </summary>
[WorkflowActionMetadata(
    Description = "Conditional if/then/else logic for workflows",
    RequiredParameters = new[] { "Condition", "ThenActions" },
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
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var condition = parameters.GetValueOrDefault("Condition");
        var thenActionsJson = parameters.GetValueOrDefault("ThenActions");
        var elseActionsJson = parameters.GetValueOrDefault("ElseActions");

        if (string.IsNullOrEmpty(condition))
        {
            return WorkflowActionResult.PermanentFailure("Conditional action is missing its 'Condition' parameter.");
        }

        var conditionResult = EvaluateCondition(condition, content);
        var actionsToExecute = conditionResult ? thenActionsJson : elseActionsJson;

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

        if (actions == null || actions.Count == 0)
        {
            return WorkflowActionResult.Success();
        }

        var availableActions = _serviceProvider.GetService<IEnumerable<IWorkflowAction>>();
        if (availableActions == null)
        {
            return WorkflowActionResult.PermanentFailure("No workflow actions are registered, so the conditional's children could not run.");
        }

        var succeededCount = 0;
        var failedTypes = new List<string>();
        var anyPermanentFailure = false;

        foreach (var childAction in actions)
        {
            var plugin = availableActions.FirstOrDefault(a => a.Type == childAction.Type);
            if (plugin == null)
            {
                _logger.LogWarning("Action type {Type} not found", childAction.Type);
                failedTypes.Add(childAction.Type);
                anyPermanentFailure = true;
                continue;
            }

            WorkflowActionResult childResult;
            try
            {
                childResult = await plugin.RunAsync(childAction.Parameters, content, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                failedTypes.Add(childAction.Type);
                continue;
            }

            if (childResult.Succeeded)
            {
                succeededCount++;
                continue;
            }

            // The child's error is dropped, not relocated: it can hold what the child was trying to
            // send, and neither the run record nor the log is a safe home for text a child action
            // composed. A log aggregator is a different place, not a safer one, same reasoning
            // WebhookAction applies to an HttpRequestException's message.
            _logger.LogWarning("Child action {Type} failed", childAction.Type);
            failedTypes.Add(childAction.Type);
            if (!childResult.Retryable)
            {
                anyPermanentFailure = true;
            }
        }

        _logger.LogInformation(
            "Conditional action executed {Branch} branch with {Count} actions, {Failed} failed",
            conditionResult ? "then" : "else", actions.Count, failedTypes.Count);

        if (failedTypes.Count == 0)
        {
            return WorkflowActionResult.Success();
        }

        var branch = conditionResult ? "then" : "else";
        var names = string.Join(", ", failedTypes.Distinct());
        var message = failedTypes.Count == actions.Count
            ? $"All {actions.Count} child action(s) failed in the {branch} branch: {names}."
            : $"{failedTypes.Count} of {actions.Count} child action(s) failed in the {branch} branch: {names}.";

        // A retry re-runs every child from the top, so it is only safe when nothing has succeeded
        // yet. Once one child has succeeded alongside a failure, retrying would resend what that
        // child already sent, which is its own defect (see the remarks above).
        if (succeededCount > 0 || anyPermanentFailure)
        {
            return WorkflowActionResult.PermanentFailure(message);
        }

        return WorkflowActionResult.Failure(message);
    }

    private bool EvaluateCondition(string condition, barakoCMS.Models.Content content)
    {
        // Simple condition evaluator for common patterns
        // Supports: {{data.Field}} == "Value", {{status}} == "Published", etc.

        try
        {
            // Extract template variable and expected value
            var parts = condition.Split(new[] { "==", "!=" }, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                _logger.LogWarning("Invalid condition format: {Condition}", condition);
                return false;
            }

            var isNotEqual = condition.Contains("!=");
            var variable = parts[0].Trim();
            var expectedValue = parts[1].Trim().Trim('"');

            // Resolve variable value
            string actualValue = "";
            if (variable.Contains("{{data."))
            {
                var fieldName = variable.Replace("{{data.", "").Replace("}}", "");
                actualValue = content.Data.GetValueOrDefault(fieldName)?.ToString() ?? "";
            }
            else if (variable.Contains("{{status}}"))
            {
                actualValue = content.Status.ToString();
            }
            else if (variable.Contains("{{contentType}}"))
            {
                actualValue = content.ContentType;
            }

            // Compare
            var result = actualValue.Equals(expectedValue, StringComparison.OrdinalIgnoreCase);
            return isNotEqual ? !result : result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error evaluating condition: {Condition}", condition);
            return false;
        }
    }

    private class ChildAction
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }
}
