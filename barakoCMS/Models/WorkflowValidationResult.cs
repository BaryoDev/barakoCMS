namespace barakoCMS.Models;

/// <summary>
/// Result of workflow validation.
/// </summary>
public class WorkflowValidationResult
{
    /// <summary>
    /// Whether the workflow is valid.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation errors.
    /// </summary>
    public List<ValidationError> Errors { get; set; } = new();

    /// <summary>
    /// The trigger spelled the way the content type declares it, when the trigger names a lifecycle
    /// transition. Null otherwise.
    /// </summary>
    /// <remarks>
    /// The engine matches TriggerEvent with an equality query and the lifecycle matches a transition
    /// name case insensitively. Storing what the caller sent would let "transition:approve" validate
    /// against a transition declared "Approve" and then never match an event, which is the failure
    /// this whole check exists to prevent, arrived at by a different road.
    /// </remarks>
    public string? NormalisedTriggerEvent { get; set; }
}

/// <summary>
/// A single validation error.
/// </summary>
public class ValidationError
{
    /// <summary>
    /// The field path with the error (e.g., "actions[0].parameters.To").
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// The error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
