namespace barakoCMS.Infrastructure.Attributes;

/// <summary>
/// Metadata attribute for workflow action plugins.
/// Provides documentation and schema information for plugin discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class WorkflowActionMetadataAttribute : Attribute
{
    /// <summary>
    /// Human-readable description of what this action does.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// List of required parameter names for this action.
    /// </summary>
    public string[] RequiredParameters { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Example JSON configuration for documentation.
    /// </summary>
    public string ExampleJson { get; set; } = string.Empty;
}
