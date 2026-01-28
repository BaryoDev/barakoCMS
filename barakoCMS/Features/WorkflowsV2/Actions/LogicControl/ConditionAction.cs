using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace barakoCMS.Features.WorkflowsV2.Actions.LogicControl;

/// <summary>
/// Conditional logic action with if/then/else branching.
/// </summary>
public class ConditionAction : BaseWorkflowAction
{
    private readonly IAdvancedConditionEvaluator _conditionEvaluator;
    private readonly IServiceProvider _serviceProvider;

    public ConditionAction(
        IAdvancedConditionEvaluator conditionEvaluator,
        IServiceProvider serviceProvider,
        ILogger<ConditionAction> logger) : base(logger)
    {
        _conditionEvaluator = conditionEvaluator;
        _serviceProvider = serviceProvider;
    }

    public override string Type => "Condition";
    public override string Name => "Condition";
    public override string Description => "Execute different actions based on conditions (if/then/else).";
    public override string Category => ActionCategories.LogicControl;

    public override bool SupportsPreHook => true;
    public override bool SupportsPostHook => true;
    public override bool CanModifyData => true;
    public override bool CanBlockOperation => true;

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            // Parse condition
            WorkflowConditionGroup? condition = null;

            if (action.Config.TryGetValue("condition", out var conditionObj))
            {
                condition = ParseCondition(conditionObj);
            }

            if (condition == null)
            {
                return Failure("No condition specified.");
            }

            // Evaluate condition
            var result = _conditionEvaluator.Evaluate(condition, context);

            Logger.LogInformation("Condition evaluated to {Result}", result);

            // Get the actions to execute
            var actionsKey = result ? "then" : "else";
            List<WorkflowActionV2>? actionsToExecute = null;

            if (action.Config.TryGetValue(actionsKey, out var actionsObj))
            {
                actionsToExecute = ParseActions(actionsObj);
            }

            if (actionsToExecute == null || actionsToExecute.Count == 0)
            {
                Logger.LogInformation("No '{Branch}' actions defined, skipping.", actionsKey);
                return Success(new Dictionary<string, object>
                {
                    ["conditionResult"] = result,
                    ["branch"] = actionsKey,
                    ["actionsExecuted"] = 0
                });
            }

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Would execute {Count} '{Branch}' actions",
                    actionsToExecute.Count, actionsKey);

                return Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["conditionResult"] = result,
                    ["branch"] = actionsKey,
                    ["actionsToExecute"] = actionsToExecute.Select(a => a.Type).ToList()
                });
            }

            // Execute child actions
            var actionRegistry = _serviceProvider.GetService(typeof(IActionRegistry)) as IActionRegistry;
            if (actionRegistry == null)
            {
                return Failure("Action registry not available.");
            }

            var executedCount = 0;
            var aggregatedResult = new ActionResult { Success = true };

            foreach (var childAction in actionsToExecute)
            {
                var actionHandler = actionRegistry.GetAction(childAction.Type);
                if (actionHandler == null)
                {
                    Logger.LogWarning("Action type '{Type}' not found, skipping.", childAction.Type);
                    continue;
                }

                var childResult = await actionHandler.ExecuteAsync(childAction, context);
                executedCount++;

                // Store result
                context.ActionResults[childAction.Id] = childResult;

                if (!childResult.Success)
                {
                    if (childAction.ContinueOnError)
                    {
                        Logger.LogWarning("Action '{Id}' failed but continuing: {Error}",
                            childAction.Id, childResult.ErrorMessage);
                    }
                    else
                    {
                        aggregatedResult.Success = false;
                        aggregatedResult.ErrorMessage = childResult.ErrorMessage;
                        break;
                    }
                }

                // Handle blocking
                if (childResult.BlockOperation)
                {
                    aggregatedResult.BlockOperation = true;
                    aggregatedResult.BlockMessage = childResult.BlockMessage;
                    break;
                }

                // Handle modified data
                if (childResult.ModifiedData != null)
                {
                    aggregatedResult.ModifiedData ??= new Dictionary<string, object>();
                    foreach (var kv in childResult.ModifiedData)
                    {
                        aggregatedResult.ModifiedData[kv.Key] = kv.Value;
                    }
                }
            }

            aggregatedResult.Output["conditionResult"] = result;
            aggregatedResult.Output["branch"] = actionsKey;
            aggregatedResult.Output["actionsExecuted"] = executedCount;

            return aggregatedResult;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to execute condition action");
            return Failure($"Failed to execute condition: {ex.Message}");
        }
    }

    private WorkflowConditionGroup? ParseCondition(object conditionObj)
    {
        if (conditionObj is WorkflowConditionGroup group)
            return group;

        if (conditionObj is JsonElement je)
        {
            return JsonSerializer.Deserialize<WorkflowConditionGroup>(je.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        if (conditionObj is string conditionStr)
        {
            return JsonSerializer.Deserialize<WorkflowConditionGroup>(conditionStr, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        return null;
    }

    private List<WorkflowActionV2>? ParseActions(object actionsObj)
    {
        if (actionsObj is List<WorkflowActionV2> list)
            return list;

        if (actionsObj is JsonElement je)
        {
            return JsonSerializer.Deserialize<List<WorkflowActionV2>>(je.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        if (actionsObj is string actionsStr)
        {
            return JsonSerializer.Deserialize<List<WorkflowActionV2>>(actionsStr, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        return null;
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("condition"))
            errors.Add("'condition' is required.");

        if (!config.ContainsKey("then") && !config.ContainsKey("else"))
            errors.Add("At least one of 'then' or 'else' must be specified.");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "condition", Type = "object", Description = "Condition group to evaluate", Required = true, SupportsTemplateVariables = false },
                new() { Name = "then", Type = "array", Description = "Actions to execute if condition is true", SupportsTemplateVariables = false },
                new() { Name = "else", Type = "array", Description = "Actions to execute if condition is false", SupportsTemplateVariables = false }
            },
            Required = new List<string> { "condition" },
            Example = @"{""condition"": {""operator"": ""AND"", ""rules"": [{""field"": ""data.Amount"", ""operator"": ""gte"", ""value"": 1000}]}, ""then"": [{""type"": ""StartApproval"", ""config"": {}}]}"
        };
    }
}
