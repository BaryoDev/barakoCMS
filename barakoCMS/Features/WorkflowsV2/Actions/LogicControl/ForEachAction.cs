using System.Text.Json;
using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.LogicControl;

/// <summary>
/// Action that iterates over a collection and executes nested actions.
/// </summary>
public class ForEachAction : BaseWorkflowAction
{
    private readonly IActionRegistry _actionRegistry;

    public override string Type => "ForEach";
    public override string Name => "For Each";
    public override string Category => ActionCategories.LogicControl;
    public override string Description => "Iterate over a collection and execute actions for each item";

    public ForEachAction(IActionRegistry actionRegistry, ILogger<ForEachAction> logger) : base(logger)
    {
        _actionRegistry = actionRegistry;
    }

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var collectionPath = GetString(action.Config, "collection", context);
        var itemVariable = GetString(action.Config, "itemVariable", context, "item");
        var indexVariable = GetString(action.Config, "indexVariable", context, "index");
        var maxIterations = GetInt(action.Config, "maxIterations", 100);
        var continueOnError = GetBool(action.Config, "continueOnError", true);

        if (string.IsNullOrEmpty(collectionPath))
        {
            return Failure("Collection path is required");
        }

        // Get collection from context
        var collection = GetCollection(collectionPath, context);

        if (collection == null || !collection.Any())
        {
            Logger.LogDebug("ForEach: Collection {Path} is empty", collectionPath);
            return Success(new Dictionary<string, object>
            {
                ["iterations"] = 0,
                ["message"] = "Collection was empty"
            });
        }

        // Get nested actions from config
        var nestedActions = GetNestedActions(action.Config);

        var results = new List<Dictionary<string, object>>();
        var errors = new List<string>();
        var index = 0;

        foreach (var item in collection.Take(maxIterations))
        {
            // Set loop variables
            context.Variables[itemVariable] = item;
            context.Variables[indexVariable] = index;

            try
            {
                foreach (var nestedAction in nestedActions)
                {
                    var actionHandler = _actionRegistry.GetAction(nestedAction.Type);
                    if (actionHandler == null)
                    {
                        Logger.LogWarning("Unknown action type in ForEach: {Type}", nestedAction.Type);
                        continue;
                    }

                    var result = await actionHandler.ExecuteAsync(nestedAction, context);

                    if (!result.Success && !continueOnError)
                    {
                        return Failure($"Action {nestedAction.Type} failed at index {index}: {result.ErrorMessage}");
                    }

                    if (!result.Success)
                    {
                        errors.Add($"Index {index}, {nestedAction.Type}: {result.ErrorMessage}");
                    }
                }

                results.Add(new Dictionary<string, object>
                {
                    ["index"] = index,
                    ["success"] = true
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ForEach iteration {Index} failed", index);

                if (!continueOnError)
                {
                    return Failure($"Iteration {index} failed: {ex.Message}");
                }

                errors.Add($"Index {index}: {ex.Message}");
            }

            index++;
        }

        // Clean up loop variables
        context.Variables.Remove(itemVariable);
        context.Variables.Remove(indexVariable);

        Logger.LogInformation("ForEach completed {Iterations} iterations with {Errors} errors", index, errors.Count);

        return Success(new Dictionary<string, object>
        {
            ["iterations"] = index,
            ["successful"] = results.Count(r => (bool)r["success"]),
            ["failed"] = errors.Count,
            ["errors"] = errors
        });
    }

    private IEnumerable<object>? GetCollection(string path, WorkflowContext context)
    {
        var parts = path.Split('.');
        object? current = parts[0] switch
        {
            "data" => context.Content.Data,
            "variables" => context.Variables,
            _ => context.Content.Data
        };

        for (int i = 1; i < parts.Length && current != null; i++)
        {
            if (current is Dictionary<string, object> dict)
            {
                dict.TryGetValue(parts[i], out current);
            }
            else if (current is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                if (je.TryGetProperty(parts[i], out var prop))
                {
                    current = prop;
                }
                else
                {
                    current = null;
                }
            }
            else
            {
                current = null;
            }
        }

        if (current is IEnumerable<object> enumerable)
        {
            return enumerable;
        }

        if (current is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Cast<object>();
        }

        return null;
    }

    private List<WorkflowActionV2> GetNestedActions(Dictionary<string, object> config)
    {
        if (!config.TryGetValue("actions", out var actionsObj))
            return new List<WorkflowActionV2>();

        if (actionsObj is List<WorkflowActionV2> actions)
            return actions;

        if (actionsObj is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<WorkflowActionV2>>(je.GetRawText()) ?? new List<WorkflowActionV2>();
        }

        return new List<WorkflowActionV2>();
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("collection"))
            errors.Add("'collection' is required");

        if (!config.ContainsKey("actions"))
            errors.Add("'actions' is required");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "collection", Type = "string", Description = "Path to collection (e.g., data.items)", Required = true },
                new() { Name = "itemVariable", Type = "string", Description = "Variable name for current item", Default = "item" },
                new() { Name = "indexVariable", Type = "string", Description = "Variable name for current index", Default = "index" },
                new() { Name = "actions", Type = "array", Description = "Actions to execute for each item", Required = true },
                new() { Name = "maxIterations", Type = "integer", Description = "Maximum iterations", Default = 100 },
                new() { Name = "continueOnError", Type = "boolean", Description = "Continue on error", Default = true }
            },
            Required = new List<string> { "collection", "actions" },
            Example = @"{""collection"": ""data.recipients"", ""actions"": [{""type"": ""SendEmail"", ""config"": {""to"": ""{{item.email}}""}}]}"
        };
    }
}
