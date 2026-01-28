namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Enhanced workflow definition with pre/post hooks, versioning, and rich conditions.
/// </summary>
public class WorkflowDefinitionV2
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 0;
    public int Version { get; set; } = 1;

    /// <summary>
    /// Trigger configuration for when this workflow executes.
    /// </summary>
    public WorkflowTrigger Trigger { get; set; } = new();

    /// <summary>
    /// Actions to execute when triggered.
    /// </summary>
    public List<WorkflowActionV2> Actions { get; set; } = new();

    /// <summary>
    /// Error handling configuration.
    /// </summary>
    public WorkflowErrorHandling ErrorHandling { get; set; } = new();

    /// <summary>
    /// Tags for organization and filtering.
    /// </summary>
    public List<string> Tags { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
}

/// <summary>
/// Workflow trigger configuration.
/// </summary>
public class WorkflowTrigger
{
    /// <summary>
    /// Content type this workflow applies to (e.g., "PurchaseOrder", "Article").
    /// Use "*" for all content types.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Event that triggers this workflow.
    /// Values: pre:create, pre:read, pre:update, pre:delete,
    ///         post:create, post:read, post:update, post:delete, post:status_change
    /// </summary>
    public string Event { get; set; } = string.Empty;

    /// <summary>
    /// Conditions that must be met for the workflow to execute.
    /// </summary>
    public WorkflowConditionGroup? Conditions { get; set; }
}

/// <summary>
/// Enhanced workflow action with configuration and control flow.
/// </summary>
public class WorkflowActionV2
{
    /// <summary>
    /// Unique ID for this action within the workflow.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Action type (e.g., "SendEmail", "SetField", "StartApproval").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name for this action step.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Action-specific configuration.
    /// </summary>
    public Dictionary<string, object> Config { get; set; } = new();

    /// <summary>
    /// Optional condition to skip this action.
    /// </summary>
    public WorkflowConditionGroup? RunIf { get; set; }

    /// <summary>
    /// Whether to continue workflow if this action fails.
    /// </summary>
    public bool ContinueOnError { get; set; } = false;

    /// <summary>
    /// Timeout for this action in seconds (0 = no timeout).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of retry attempts on failure.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Delay between retries in seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 5;
}

/// <summary>
/// Error handling configuration for workflow execution.
/// </summary>
public class WorkflowErrorHandling
{
    /// <summary>
    /// Behavior on action failure: "stop", "continue", "retry".
    /// </summary>
    public string OnActionFailure { get; set; } = "continue";

    /// <summary>
    /// Email addresses to notify on workflow failure.
    /// </summary>
    public List<string> NotifyOnFailure { get; set; } = new();

    /// <summary>
    /// Global retry count for the entire workflow.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    /// Delay between workflow retries in seconds.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 60;
}

/// <summary>
/// Workflow execution mode.
/// </summary>
public enum WorkflowExecutionMode
{
    /// <summary>
    /// Execute synchronously, blocking the request.
    /// </summary>
    Synchronous,

    /// <summary>
    /// Queue for async execution via RabbitMQ.
    /// </summary>
    Asynchronous
}
