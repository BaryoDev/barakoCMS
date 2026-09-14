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
    /// Parameter names this action reads when present but does not need.
    /// </summary>
    /// <remarks>
    /// Not the parameters the runner adds on its own (RunId, IdempotencyKey and the like), only the
    /// ones a person writing the workflow would set.
    /// </remarks>
    public string[] OptionalParameters { get; set; } = Array.Empty<string>();

    /// <summary>
    /// The group the workflow builder lists this action under. Leave it unset and the action reports
    /// no group.
    /// </summary>
    public WorkflowActionGroup Group { get; set; } = WorkflowActionGroup.Unspecified;

    /// <summary>
    /// Example JSON configuration for documentation.
    /// </summary>
    public string ExampleJson { get; set; } = string.Empty;
}
