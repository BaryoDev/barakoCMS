namespace barakoCMS.Models;

/// <summary>
/// Metadata about a workflow action plugin.
/// </summary>
public class WorkflowActionMetadata
{
    /// <summary>
    /// The unique action type identifier.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description of the action.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// List of required parameter names.
    /// </summary>
    public List<string> RequiredParameters { get; set; } = new();

    /// <summary>
    /// Example configuration JSON.
    /// </summary>
    public string ExampleConfiguration { get; set; } = string.Empty;
}
