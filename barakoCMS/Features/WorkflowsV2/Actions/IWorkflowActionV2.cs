using barakoCMS.Features.WorkflowsV2.Models;

namespace barakoCMS.Features.WorkflowsV2.Actions;

/// <summary>
/// Interface for workflow action plugins.
/// </summary>
public interface IWorkflowActionV2
{
    /// <summary>
    /// Unique type identifier for this action.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Description of what this action does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Category for grouping actions.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Whether this action can be used in pre-hooks.
    /// </summary>
    bool SupportsPreHook { get; }

    /// <summary>
    /// Whether this action can be used in post-hooks.
    /// </summary>
    bool SupportsPostHook { get; }

    /// <summary>
    /// Whether this action can modify content data (pre-hooks).
    /// </summary>
    bool CanModifyData { get; }

    /// <summary>
    /// Whether this action can block the operation (pre-hooks).
    /// </summary>
    bool CanBlockOperation { get; }

    /// <summary>
    /// Execute the action.
    /// </summary>
    Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context);

    /// <summary>
    /// Validate the action configuration.
    /// </summary>
    List<string> ValidateConfig(Dictionary<string, object> config);

    /// <summary>
    /// Get the JSON schema for this action's configuration.
    /// </summary>
    ActionConfigSchema GetConfigSchema();
}

/// <summary>
/// Schema for action configuration.
/// </summary>
public class ActionConfigSchema
{
    public string Type { get; set; } = string.Empty;
    public List<ActionConfigProperty> Properties { get; set; } = new();
    public List<string> Required { get; set; } = new();
    public string? Example { get; set; }
}

/// <summary>
/// Property in action configuration schema.
/// </summary>
public class ActionConfigProperty
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; }
    public object? Default { get; set; }
    public List<string>? Enum { get; set; }
    public bool SupportsTemplateVariables { get; set; } = true;
}

/// <summary>
/// Categories for organizing actions.
/// </summary>
public static class ActionCategories
{
    public const string Communication = "Communication";
    public const string DataOperations = "Data Operations";
    public const string ApprovalWorkflow = "Approval & Workflow";
    public const string ExternalIntegration = "External Integration";
    public const string LogicControl = "Logic & Control";
    public const string Validation = "Validation";
}
