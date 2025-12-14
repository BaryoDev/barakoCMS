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
public class ConditionalAction : IWorkflowAction
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

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var condition = parameters.GetValueOrDefault("Condition");
        var thenActionsJson = parameters.GetValueOrDefault("ThenActions");
        var elseActionsJson = parameters.GetValueOrDefault("ElseActions");

        if (string.IsNullOrEmpty(condition))
        {
            _logger.LogWarning("Conditional action missing 'Condition' parameter");
            return;
        }

        try
        {
            var conditionResult = EvaluateCondition(condition, content);
            var actionsToExecute = conditionResult ? thenActionsJson : elseActionsJson;

            if (string.IsNullOrEmpty(actionsToExecute))
            {
                _logger.LogInformation(
                    "Conditional evaluated to {Result} but no actions defined for that branch",
                    conditionResult);
                return;
            }

            // Parse and execute child actions
            var actions = JsonSerializer.Deserialize<List<ChildAction>>(actionsToExecute);
            if (actions == null || !actions.Any())
            {
                return;
            }

            // Get available actions from service provider
            var availableActions = _serviceProvider.GetService<IEnumerable<IWorkflowAction>>();
            if (availableActions == null)
            {
                _logger.LogWarning("No workflow actions registered");
                return;
            }

            foreach (var childAction in actions)
            {
                var plugin = availableActions.FirstOrDefault(a => a.Type == childAction.Type);
                if (plugin == null)
                {
                    _logger.LogWarning("Action type {Type} not found", childAction.Type);
                    continue;
                }

                await plugin.ExecuteAsync(childAction.Parameters, content, ct);
            }

            _logger.LogInformation(
                "Conditional action executed {Branch} branch with {Count} actions",
                conditionResult ? "then" : "else", actions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute conditional action");
        }
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
