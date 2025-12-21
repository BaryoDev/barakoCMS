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
