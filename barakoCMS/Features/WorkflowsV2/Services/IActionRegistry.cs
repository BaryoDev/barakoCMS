using barakoCMS.Features.WorkflowsV2.Actions;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Registry for workflow action plugins.
/// </summary>
public interface IActionRegistry
{
    /// <summary>
    /// Get an action handler by type.
    /// </summary>
    IWorkflowActionV2? GetAction(string type);

    /// <summary>
    /// Get all registered actions.
    /// </summary>
    IEnumerable<IWorkflowActionV2> GetAllActions();

    /// <summary>
    /// Get actions by category.
    /// </summary>
    IEnumerable<IWorkflowActionV2> GetActionsByCategory(string category);

    /// <summary>
    /// Get action metadata for UI.
    /// </summary>
    IEnumerable<ActionMetadata> GetActionMetadata();
}

/// <summary>
/// Metadata about an action for display purposes.
/// </summary>
public class ActionMetadata
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool SupportsPreHook { get; set; }
    public bool SupportsPostHook { get; set; }
    public bool CanModifyData { get; set; }
    public bool CanBlockOperation { get; set; }
    public ActionConfigSchema Schema { get; set; } = new();
}
