namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Enhanced execution log for workflow runs.
/// </summary>
public class WorkflowExecutionLogV2
{
    public Guid Id { get; set; }

    /// <summary>
    /// The workflow that was executed.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Workflow name at time of execution.
    /// </summary>
    public string WorkflowName { get; set; } = string.Empty;

    /// <summary>
    /// Workflow version that was executed.
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// The content that triggered the workflow.
    /// </summary>
    public Guid ContentId { get; set; }

    /// <summary>
    /// Content type.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Event that triggered the workflow.
    /// </summary>
    public string TriggerEvent { get; set; } = string.Empty;

    /// <summary>
    /// User who triggered the workflow (if any).
    /// </summary>
    public Guid? TriggeredBy { get; set; }

    /// <summary>
    /// When execution started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When execution completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Total execution duration.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Execution status.
    /// </summary>
    public WorkflowExecutionStatus Status { get; set; } = WorkflowExecutionStatus.Running;

    /// <summary>
    /// Whether this was a dry-run execution.
    /// </summary>
    public bool IsDryRun { get; set; }

    /// <summary>
    /// Whether this was async execution.
    /// </summary>
    public bool IsAsync { get; set; }

    /// <summary>
    /// RabbitMQ message ID if async.
    /// </summary>
    public string? QueueMessageId { get; set; }

    /// <summary>
    /// Number of retry attempts.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Stack trace if failed.
    /// </summary>
    public string? ErrorStackTrace { get; set; }

    /// <summary>
    /// Per-action execution logs.
    /// </summary>
    public List<ActionExecutionLogV2> Actions { get; set; } = new();

    /// <summary>
    /// Snapshot of content data at execution time.
    /// </summary>
    public Dictionary<string, object>? ContentSnapshot { get; set; }

    /// <summary>
    /// Variables resolved during execution.
    /// </summary>
    public Dictionary<string, object>? ResolvedVariables { get; set; }

    /// <summary>
    /// Correlation ID for tracing.
    /// </summary>
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Execution log for a single action.
/// </summary>
public class ActionExecutionLogV2
{
    /// <summary>
    /// Action ID within the workflow.
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// Action type.
    /// </summary>
    public string ActionType { get; set; } = string.Empty;

    /// <summary>
    /// Action name.
    /// </summary>
    public string ActionName { get; set; } = string.Empty;

    /// <summary>
    /// When the action started.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// When the action completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Action duration.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Action status.
    /// </summary>
    public ActionExecutionStatus Status { get; set; } = ActionExecutionStatus.Pending;

    /// <summary>
    /// Whether the action was skipped due to condition.
    /// </summary>
    public bool WasSkipped { get; set; }

    /// <summary>
    /// Reason for skipping if applicable.
    /// </summary>
    public string? SkipReason { get; set; }

    /// <summary>
    /// Retry attempt number.
    /// </summary>
    public int RetryAttempt { get; set; }

    /// <summary>
    /// Input parameters (resolved).
    /// </summary>
    public Dictionary<string, object>? InputParameters { get; set; }

    /// <summary>
    /// Output/result from the action.
    /// </summary>
    public Dictionary<string, object>? Output { get; set; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Error details/stack trace.
    /// </summary>
    public string? ErrorDetails { get; set; }
}

/// <summary>
/// Workflow execution status.
/// </summary>
public enum WorkflowExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
    PartiallyCompleted
}

/// <summary>
/// Action execution status.
/// </summary>
public enum ActionExecutionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    TimedOut,
    Retrying
}
