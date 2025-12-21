namespace barakoCMS.Models;

/// <summary>
/// Execution log for a workflow run.
/// </summary>
public class WorkflowExecutionLog
{
    /// <summary>
    /// Unique log ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The workflow that was executed.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// The content that triggered the workflow.
    /// </summary>
    public Guid ContentId { get; set; }

    /// <summary>
    /// When the workflow was executed.
    /// </summary>
    public DateTime ExecutedAt { get; set; }

    /// <summary>
    /// Whether this was a dry-run (no side effects).
    /// </summary>
    public bool IsDryRun { get; set; }

    /// <summary>
    /// Overall execution success.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Total execution duration.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Logs for each action executed.
    /// </summary>
    public List<ActionExecutionLog> Actions { get; set; } = new();
}

/// <summary>
/// Execution log for a single action.
/// </summary>
public class ActionExecutionLog
{
    /// <summary>
    /// The action type that was executed.
    /// </summary>
    public string ActionType { get; set; } = string.Empty;

    /// <summary>
    /// Whether the action succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if action failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Resolved parameter values (after template substitution).
    /// </summary>
    public Dictionary<string, string> ResolvedParameters { get; set; } = new();

    /// <summary>
    /// How long the action took to execute.
    /// </summary>
    public TimeSpan Duration { get; set; }
}
