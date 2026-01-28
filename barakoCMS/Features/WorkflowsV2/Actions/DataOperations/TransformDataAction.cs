using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.DataOperations;

/// <summary>
/// Action that transforms data by setting workflow variables.
/// </summary>
public class TransformDataAction : BaseWorkflowAction
{
    public override string Type => "TransformData";
    public override string Name => "Transform Data";
    public override string Category => ActionCategories.DataOperations;
    public override string Description => "Transform or compute data values and store in workflow variables";

    public TransformDataAction(ILogger<TransformDataAction> logger) : base(logger) { }

    public override Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var outputs = GetDictionary(action.Config, "outputs");
        var results = new Dictionary<string, object>();

        foreach (var (key, value) in outputs)
        {
            try
            {
                var resolved = value != null
                    ? ResolveTemplateVariables(value.ToString() ?? "", context)
                    : "";

                context.Variables[key] = resolved;
                results[key] = resolved;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to transform value for {Key}", key);
                results[key] = $"ERROR: {ex.Message}";
            }
        }

        // Handle operations
        var operations = GetDictionary(action.Config, "operations");
        foreach (var (varName, opConfig) in operations)
        {
            try
            {
                if (opConfig is not Dictionary<string, object> op)
                    continue;

                var operation = op.GetValueOrDefault("operation")?.ToString() ?? "";
                var input = op.GetValueOrDefault("input")?.ToString() ?? "";
                var resolvedInput = ResolveTemplateVariables(input, context);

                var result = ExecuteOperation(operation, resolvedInput, op, context);
                context.Variables[varName] = result;
                results[varName] = result ?? "";
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed operation for {Key}", varName);
                results[varName] = $"ERROR: {ex.Message}";
            }
        }

        return Task.FromResult(Success(results));
    }

    private object? ExecuteOperation(string operation, string input, Dictionary<string, object> config, WorkflowContext context)
    {
        return operation.ToLowerInvariant() switch
        {
            "uppercase" => input.ToUpperInvariant(),
            "lowercase" => input.ToLowerInvariant(),
            "trim" => input.Trim(),
            "length" => input.Length,
            "now" => DateTime.UtcNow.ToString("o"),
            "today" => DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
            "add" => Add(config),
            "subtract" => Subtract(config),
            "multiply" => Multiply(config),
            "divide" => Divide(config),
            "concat" => Concat(config, context),
            "default" => string.IsNullOrEmpty(input)
                ? ResolveTemplateVariables(config.GetValueOrDefault("defaultValue")?.ToString() ?? "", context)
                : input,
            _ => input
        };
    }

    private double Add(Dictionary<string, object> config)
    {
        var a = ParseDouble(config.GetValueOrDefault("a")?.ToString());
        var b = ParseDouble(config.GetValueOrDefault("b")?.ToString());
        return a + b;
    }

    private double Subtract(Dictionary<string, object> config)
    {
        var a = ParseDouble(config.GetValueOrDefault("a")?.ToString());
        var b = ParseDouble(config.GetValueOrDefault("b")?.ToString());
        return a - b;
    }

    private double Multiply(Dictionary<string, object> config)
    {
        var a = ParseDouble(config.GetValueOrDefault("a")?.ToString());
        var b = ParseDouble(config.GetValueOrDefault("b")?.ToString());
        return a * b;
    }

    private double Divide(Dictionary<string, object> config)
    {
        var a = ParseDouble(config.GetValueOrDefault("a")?.ToString());
        var b = ParseDouble(config.GetValueOrDefault("b")?.ToString());
        return b != 0 ? a / b : 0;
    }

    private string Concat(Dictionary<string, object> config, WorkflowContext context)
    {
        var values = config.GetValueOrDefault("values") as IEnumerable<object>;
        if (values == null) return "";

        return string.Join("", values.Select(v => ResolveTemplateVariables(v?.ToString() ?? "", context)));
    }

    private double ParseDouble(string? input)
    {
        return double.TryParse(input, out var result) ? result : 0;
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        return new List<string>(); // No required fields
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "outputs", Type = "object", Description = "Key-value pairs of variable name to template value" },
                new() { Name = "operations", Type = "object", Description = "Key-value pairs of variable name to operation config" }
            },
            Example = @"{""outputs"": {""fullName"": ""{{data.firstName}} {{data.lastName}}""}, ""operations"": {""total"": {""operation"": ""add"", ""a"": ""{{data.price}}"", ""b"": ""{{data.tax}}""}}}"
        };
    }
}
