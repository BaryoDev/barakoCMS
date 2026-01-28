using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions;

/// <summary>
/// Base class for workflow actions with common functionality.
/// </summary>
public abstract class BaseWorkflowAction : IWorkflowActionV2
{
    protected readonly ILogger Logger;

    protected BaseWorkflowAction(ILogger logger)
    {
        Logger = logger;
    }

    public abstract string Type { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Category { get; }

    public virtual bool SupportsPreHook => false;
    public virtual bool SupportsPostHook => true;
    public virtual bool CanModifyData => false;
    public virtual bool CanBlockOperation => false;

    public abstract Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context);
    public abstract List<string> ValidateConfig(Dictionary<string, object> config);
    public abstract ActionConfigSchema GetConfigSchema();

    /// <summary>
    /// Get a required string configuration value.
    /// </summary>
    protected string GetRequiredString(Dictionary<string, object> config, string key, WorkflowContext context)
    {
        var value = GetString(config, key, context);
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"Required configuration '{key}' is missing or empty.");
        return value;
    }

    /// <summary>
    /// Get an optional string configuration value.
    /// </summary>
    protected string GetString(Dictionary<string, object> config, string key, WorkflowContext context, string defaultValue = "")
    {
        if (!config.TryGetValue(key, out var value))
            return defaultValue;

        var strValue = ConvertToString(value);
        return ResolveTemplateVariables(strValue, context);
    }

    /// <summary>
    /// Get an optional integer configuration value.
    /// </summary>
    protected int GetInt(Dictionary<string, object> config, string key, int defaultValue = 0)
    {
        if (!config.TryGetValue(key, out var value))
            return defaultValue;

        if (value is int i)
            return i;

        if (value is long l)
            return (int)l;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.GetInt32();

        if (int.TryParse(ConvertToString(value), out var parsed))
            return parsed;

        return defaultValue;
    }

    /// <summary>
    /// Get an optional boolean configuration value.
    /// </summary>
    protected bool GetBool(Dictionary<string, object> config, string key, bool defaultValue = false)
    {
        if (!config.TryGetValue(key, out var value))
            return defaultValue;

        if (value is bool b)
            return b;

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True)
                return true;
            if (je.ValueKind == JsonValueKind.False)
                return false;
        }

        var strValue = ConvertToString(value).ToLowerInvariant();
        return strValue == "true" || strValue == "1" || strValue == "yes";
    }

    /// <summary>
    /// Get a list of strings from configuration.
    /// </summary>
    protected List<string> GetStringList(Dictionary<string, object> config, string key, WorkflowContext context)
    {
        if (!config.TryGetValue(key, out var value))
            return new List<string>();

        var result = new List<string>();

        if (value is IEnumerable<object> enumerable)
        {
            foreach (var item in enumerable)
            {
                var strItem = ResolveTemplateVariables(ConvertToString(item), context);
                if (!string.IsNullOrEmpty(strItem))
                    result.Add(strItem);
            }
        }
        else if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in je.EnumerateArray())
            {
                var strItem = ResolveTemplateVariables(item.GetString() ?? "", context);
                if (!string.IsNullOrEmpty(strItem))
                    result.Add(strItem);
            }
        }
        else
        {
            // Comma-separated string
            var strValue = ConvertToString(value);
            var items = strValue.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var item in items)
            {
                var strItem = ResolveTemplateVariables(item.Trim(), context);
                if (!string.IsNullOrEmpty(strItem))
                    result.Add(strItem);
            }
        }

        return result;
    }

    /// <summary>
    /// Get a dictionary from configuration.
    /// </summary>
    protected Dictionary<string, object> GetDictionary(Dictionary<string, object> config, string key)
    {
        if (!config.TryGetValue(key, out var value))
            return new Dictionary<string, object>();

        if (value is Dictionary<string, object> dict)
            return dict;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(je.GetRawText())
                ?? new Dictionary<string, object>();
        }

        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Resolve template variables in a string.
    /// </summary>
    protected string ResolveTemplateVariables(string template, WorkflowContext context)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        // Match {{variable}} patterns
        var regex = new Regex(@"\{\{([^}]+)\}\}");

        return regex.Replace(template, match =>
        {
            var variable = match.Groups[1].Value.Trim();
            var value = ResolveVariable(variable, context);
            return value?.ToString() ?? "";
        });
    }

    /// <summary>
    /// Resolve a single variable.
    /// </summary>
    protected object? ResolveVariable(string variable, WorkflowContext context)
    {
        var parts = variable.Split('.');

        // Special system variables
        if (variable.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            var systemVar = variable.Substring(7).ToLowerInvariant();
            return systemVar switch
            {
                "now" => DateTime.UtcNow.ToString("O"),
                "today" => DateTime.UtcNow.Date.ToString("yyyy-MM-dd"),
                "baseurl" => GetBaseUrl(context),
                "approvallink" => context.Variables.GetValueOrDefault("approvalLink"),
                "viewlink" => $"{GetBaseUrl(context)}/contents/{context.Content.Id}",
                _ => null
            };
        }

        // Content fields
        if (variable.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
        {
            var field = variable.Substring(5);
            return context.Content.Data.GetValueOrDefault(field);
        }

        // User fields
        if (variable.StartsWith("user.", StringComparison.OrdinalIgnoreCase) && context.User != null)
        {
            var field = variable.Substring(5).ToLowerInvariant();
            return field switch
            {
                "id" => context.User.Id,
                "username" => context.User.Username,
                "email" => context.User.Email,
                _ => null
            };
        }

        // Previous content
        if (variable.StartsWith("previous.", StringComparison.OrdinalIgnoreCase) && context.PreviousContent != null)
        {
            var field = variable.Substring(9);
            if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            {
                return context.PreviousContent.Data.GetValueOrDefault(field.Substring(5));
            }
        }

        // Action results
        if (variable.StartsWith("action.", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = variable.Substring(7);
            var dotIndex = remainder.IndexOf('.');
            if (dotIndex > 0)
            {
                var actionId = remainder.Substring(0, dotIndex);
                var field = remainder.Substring(dotIndex + 1);
                if (context.ActionResults.TryGetValue(actionId, out var result))
                {
                    return result.Output.GetValueOrDefault(field);
                }
            }
        }

        // Workflow variables
        if (context.Variables.TryGetValue(variable, out var varValue))
            return varValue;

        // Direct content properties
        return parts[0].ToLowerInvariant() switch
        {
            "id" => context.Content.Id,
            "contenttype" => context.Content.ContentType,
            "status" => context.Content.Status.ToString(),
            "createdat" => context.Content.CreatedAt,
            "updatedat" => context.Content.UpdatedAt,
            _ => context.Content.Data.GetValueOrDefault(variable)
        };
    }

    private string GetBaseUrl(WorkflowContext context)
    {
        if (context.HttpContext != null)
        {
            var request = context.HttpContext.Request;
            return $"{request.Scheme}://{request.Host}";
        }
        return "http://localhost";
    }

    private string ConvertToString(object? value)
    {
        if (value == null)
            return "";

        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => je.GetString() ?? "",
                JsonValueKind.Number => je.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => je.GetRawText()
            };
        }

        return value.ToString() ?? "";
    }

    /// <summary>
    /// Create a success result.
    /// </summary>
    protected ActionResult Success(Dictionary<string, object>? output = null)
    {
        return new ActionResult
        {
            Success = true,
            Output = output ?? new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Create a failure result.
    /// </summary>
    protected ActionResult Failure(string message)
    {
        return new ActionResult
        {
            Success = false,
            ErrorMessage = message
        };
    }

    /// <summary>
    /// Create a block result (for pre-hooks).
    /// </summary>
    protected ActionResult Block(string message)
    {
        return new ActionResult
        {
            Success = false,
            BlockOperation = true,
            BlockMessage = message
        };
    }
}
