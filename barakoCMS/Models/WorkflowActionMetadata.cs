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
    /// Parameter names the action reads when present but does not need.
    /// </summary>
    public List<string> OptionalParameters { get; set; } = new();

    /// <summary>
    /// The required and optional parameter names whose value the API leaves out when it returns a
    /// workflow, so a value entered for one cannot be read back.
    /// </summary>
    public List<string> SecretParameters { get; set; } = new();

    /// <summary>
    /// Example configuration JSON.
    /// </summary>
    public string ExampleConfiguration { get; set; } = string.Empty;
}
