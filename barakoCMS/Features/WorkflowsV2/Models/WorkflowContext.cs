namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Context passed to workflow actions during execution.
/// </summary>
public class WorkflowContext
{
    /// <summary>
    /// The workflow being executed.
    /// </summary>
    public WorkflowDefinitionV2 Workflow { get; set; } = null!;

    /// <summary>
    /// Current execution log.
    /// </summary>
    public WorkflowExecutionLogV2 ExecutionLog { get; set; } = null!;

    /// <summary>
    /// The content that triggered the workflow.
    /// </summary>
    public barakoCMS.Models.Content Content { get; set; } = null!;

    /// <summary>
    /// Previous content state (for updates).
    /// </summary>
    public barakoCMS.Models.Content? PreviousContent { get; set; }

    /// <summary>
    /// Current user executing the operation.
    /// </summary>
    public barakoCMS.Models.User? User { get; set; }

    /// <summary>
    /// The trigger event type.
    /// </summary>
    public string TriggerEvent { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a dry-run execution.
    /// </summary>
    public bool IsDryRun { get; set; }

    /// <summary>
    /// Variables accumulated during execution.
    /// Actions can add to this for use by subsequent actions.
    /// </summary>
    public Dictionary<string, object> Variables { get; set; } = new();

    /// <summary>
    /// Results from previous actions (keyed by action ID).
    /// </summary>
    public Dictionary<string, ActionResult> ActionResults { get; set; } = new();

    /// <summary>
    /// HTTP context for the current request.
    /// </summary>
    public Microsoft.AspNetCore.Http.HttpContext? HttpContext { get; set; }

    /// <summary>
    /// Correlation ID for tracing.
    /// </summary>
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Service provider for resolving dependencies.
    /// </summary>
    public IServiceProvider ServiceProvider { get; set; } = null!;
}

/// <summary>
/// Result from an action execution.
/// </summary>
public class ActionResult
{
    /// <summary>
    /// Whether the action succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Output data from the action.
    /// </summary>
    public Dictionary<string, object> Output { get; set; } = new();

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether to stop the workflow.
    /// </summary>
    public bool StopWorkflow { get; set; }

    /// <summary>
    /// Whether to block the operation (pre-hooks only).
    /// </summary>
    public bool BlockOperation { get; set; }

    /// <summary>
    /// Error message for blocking (shown to user).
    /// </summary>
    public string? BlockMessage { get; set; }

    /// <summary>
    /// Modified content data (pre-hooks can modify).
    /// </summary>
    public Dictionary<string, object>? ModifiedData { get; set; }

    /// <summary>
    /// Created content IDs (for CreateContent action).
    /// </summary>
    public List<Guid>? CreatedContentIds { get; set; }

    /// <summary>
    /// Approval request ID (for StartApproval action).
    /// </summary>
    public Guid? ApprovalRequestId { get; set; }
}

/// <summary>
/// Result from pre-hook execution.
/// </summary>
public class PreHookResult
{
    /// <summary>
    /// Whether to continue with the operation.
    /// </summary>
    public bool Continue { get; set; } = true;

    /// <summary>
    /// Error message if blocked.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Modified request data.
    /// </summary>
    public Dictionary<string, object>? ModifiedData { get; set; }

    /// <summary>
    /// Additional fields to set.
    /// </summary>
    public Dictionary<string, object>? AdditionalFields { get; set; }

    /// <summary>
    /// Validation errors.
    /// </summary>
    public List<ValidationError>? ValidationErrors { get; set; }
}

/// <summary>
/// Validation error from pre-hook.
/// </summary>
public class ValidationError
{
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Code { get; set; }
}
