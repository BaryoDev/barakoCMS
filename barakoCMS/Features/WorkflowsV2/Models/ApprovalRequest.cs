namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Approval request for content requiring authorization.
/// </summary>
public class ApprovalRequest
{
    public Guid Id { get; set; }

    /// <summary>
    /// Content that requires approval.
    /// </summary>
    public Guid ContentId { get; set; }

    /// <summary>
    /// Content type of the item.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Workflow that created this approval request.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Action ID within the workflow.
    /// </summary>
    public string ActionId { get; set; } = string.Empty;

    /// <summary>
    /// User who requested the approval.
    /// </summary>
    public Guid RequestedBy { get; set; }

    /// <summary>
    /// When the request was created.
    /// </summary>
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Title/subject of the approval request.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Description/reason for the request.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// List of approvers.
    /// </summary>
    public List<Approver> Approvers { get; set; } = new();

    /// <summary>
    /// Type of approval required.
    /// </summary>
    public ApprovalType ApprovalType { get; set; } = ApprovalType.Any;

    /// <summary>
    /// Threshold for "threshold" approval type.
    /// </summary>
    public int? ApprovalThreshold { get; set; }

    /// <summary>
    /// Overall status of the approval request.
    /// </summary>
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    /// <summary>
    /// When the request expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// When to send a reminder.
    /// </summary>
    public DateTime? ReminderAt { get; set; }

    /// <summary>
    /// Who to escalate to if not approved in time.
    /// </summary>
    public List<string> EscalateTo { get; set; } = new();

    /// <summary>
    /// Actions to execute when approved.
    /// </summary>
    public ApprovalOutcomeActions OnApprove { get; set; } = new();

    /// <summary>
    /// Actions to execute when rejected.
    /// </summary>
    public ApprovalOutcomeActions OnReject { get; set; } = new();

    /// <summary>
    /// Actions to execute when expired.
    /// </summary>
    public ApprovalOutcomeActions OnExpire { get; set; } = new();

    /// <summary>
    /// When the request was completed.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Secure token for approval links.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Individual approver in an approval request.
/// </summary>
public class Approver
{
    /// <summary>
    /// User ID of the approver.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// Role that can approve (any user with this role).
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Email of the approver (for external approvers).
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// Display name for the approver.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Order for sequential approvals.
    /// </summary>
    public int Order { get; set; } = 0;

    /// <summary>
    /// Current status of this approver.
    /// </summary>
    public ApproverStatus Status { get; set; } = ApproverStatus.Pending;

    /// <summary>
    /// When the approver responded.
    /// </summary>
    public DateTime? RespondedAt { get; set; }

    /// <summary>
    /// Comments from the approver.
    /// </summary>
    public string? Comments { get; set; }

    /// <summary>
    /// Decision made by the approver.
    /// </summary>
    public ApprovalDecision? Decision { get; set; }
}

/// <summary>
/// Type of approval logic.
/// </summary>
public enum ApprovalType
{
    /// <summary>
    /// Any one approver can approve.
    /// </summary>
    Any,

    /// <summary>
    /// All approvers must approve.
    /// </summary>
    All,

    /// <summary>
    /// Approvers must approve in order.
    /// </summary>
    Sequential,

    /// <summary>
    /// N of M approvers must approve.
    /// </summary>
    Threshold
}

/// <summary>
/// Overall status of an approval request.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Cancelled
}

/// <summary>
/// Status of an individual approver.
/// </summary>
public enum ApproverStatus
{
    Pending,
    Notified,
    Approved,
    Rejected,
    Delegated,
    Skipped
}

/// <summary>
/// Decision made by an approver.
/// </summary>
public enum ApprovalDecision
{
    Approve,
    Reject,
    RequestChanges,
    Delegate
}

/// <summary>
/// Actions to execute on approval outcome.
/// </summary>
public class ApprovalOutcomeActions
{
    /// <summary>
    /// Status to set on the content.
    /// </summary>
    public string? SetStatus { get; set; }

    /// <summary>
    /// Fields to update.
    /// </summary>
    public Dictionary<string, object>? SetFields { get; set; }

    /// <summary>
    /// Workflow to trigger.
    /// </summary>
    public Guid? TriggerWorkflow { get; set; }

    /// <summary>
    /// Whether to notify the requestor.
    /// </summary>
    public bool NotifyRequestor { get; set; } = true;

    /// <summary>
    /// Email template to use for notification.
    /// </summary>
    public string? EmailTemplate { get; set; }

    /// <summary>
    /// Additional users to notify.
    /// </summary>
    public List<string>? NotifyUsers { get; set; }
}
