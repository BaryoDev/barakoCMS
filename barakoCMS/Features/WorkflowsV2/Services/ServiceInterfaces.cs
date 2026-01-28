using barakoCMS.Features.WorkflowsV2.Actions.Communication;
using barakoCMS.Features.WorkflowsV2.Models;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Service for managing email templates.
/// </summary>
public interface IEmailTemplateService
{
    /// <summary>
    /// Get a template by name.
    /// </summary>
    Task<EmailTemplate?> GetTemplateAsync(string name);

    /// <summary>
    /// List all available templates.
    /// </summary>
    Task<List<EmailTemplate>> ListTemplatesAsync();

    /// <summary>
    /// Render a template with variables.
    /// </summary>
    string RenderTemplate(string template, Dictionary<string, object> variables);

    /// <summary>
    /// Save a template.
    /// </summary>
    Task SaveTemplateAsync(EmailTemplate template);

    /// <summary>
    /// Delete a template.
    /// </summary>
    Task DeleteTemplateAsync(string name);
}

/// <summary>
/// Email template model.
/// </summary>
public class EmailTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Variables { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Service for sending emails.
/// </summary>
public interface IEmailSenderService
{
    /// <summary>
    /// Send an email.
    /// </summary>
    Task SendAsync(EmailMessage message);

    /// <summary>
    /// Send an email with simple parameters.
    /// </summary>
    Task SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? from = null,
        string? replyTo = null,
        List<EmailAttachment>? attachments = null,
        CancellationToken ct = default);

    /// <summary>
    /// Send an email to multiple recipients.
    /// </summary>
    Task SendEmailAsync(
        List<string> to,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? from = null,
        string? replyTo = null,
        List<EmailAttachment>? attachments = null,
        CancellationToken ct = default);
}

/// <summary>
/// Email message.
/// </summary>
public class EmailMessage
{
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string? TextBody { get; set; }
    public string? From { get; set; }
    public string? ReplyTo { get; set; }
    public List<EmailAttachment>? Attachments { get; set; }
}

/// <summary>
/// Email attachment.
/// </summary>
public class EmailAttachment
{
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Content { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Service for managing action credentials.
/// </summary>
public interface ICredentialService
{
    /// <summary>
    /// Get a credential by name.
    /// </summary>
    Task<ActionCredential?> GetCredentialAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// List all credentials (metadata only, no secrets).
    /// </summary>
    Task<List<ActionCredential>> ListCredentialsAsync(CancellationToken ct = default);

    /// <summary>
    /// Save a credential (encrypts sensitive data).
    /// </summary>
    Task SaveCredentialAsync(ActionCredential credential, CancellationToken ct = default);

    /// <summary>
    /// Delete a credential by name.
    /// </summary>
    Task DeleteCredentialAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Get access token for a credential (refreshes OAuth2 if needed).
    /// </summary>
    Task<string?> GetAccessTokenAsync(string credentialName, CancellationToken ct = default);

    /// <summary>
    /// Initiate OAuth2 flow.
    /// </summary>
    Task<string> InitiateOAuth2FlowAsync(string credentialName, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Complete OAuth2 flow with authorization code.
    /// </summary>
    Task<bool> CompleteOAuth2FlowAsync(string credentialName, string code, string state, CancellationToken ct = default);
}

/// <summary>
/// Service for managing approval requests.
/// </summary>
public interface IApprovalService
{
    /// <summary>
    /// Create an approval request from a full request object.
    /// </summary>
    Task<ApprovalRequest> CreateApprovalAsync(ApprovalRequest request, CancellationToken ct = default);

    /// <summary>
    /// Create an approval request.
    /// </summary>
    Task<ApprovalRequest> CreateApprovalAsync(
        string title,
        string description,
        Guid workflowExecutionId,
        string contentType,
        Guid contentId,
        List<Guid> approverIds,
        ApprovalType approvalType = ApprovalType.Any,
        int? threshold = null,
        DateTime? expiresAt = null,
        Dictionary<string, object>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Get an approval request by ID.
    /// </summary>
    Task<ApprovalRequest?> GetApprovalAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// List pending approvals for a user.
    /// </summary>
    Task<List<ApprovalRequest>> GetPendingApprovalsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Get approvals by content.
    /// </summary>
    Task<List<ApprovalRequest>> GetApprovalsByContentAsync(string contentType, Guid contentId, CancellationToken ct = default);

    /// <summary>
    /// Submit an approval response.
    /// </summary>
    Task<ApprovalResult> SubmitResponseAsync(Guid approvalId, Guid userId, bool approved, string? comments = null, CancellationToken ct = default);

    /// <summary>
    /// Expire overdue approvals.
    /// </summary>
    Task<int> ExpireOverdueApprovalsAsync(CancellationToken ct = default);
}

/// <summary>
/// Result of an approval action.
/// </summary>
public class ApprovalResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public ApprovalStatus NewStatus { get; set; }
}

/// <summary>
/// Service for workflow versioning.
/// </summary>
public interface IWorkflowVersionService
{
    /// <summary>
    /// Create a new version of a workflow.
    /// </summary>
    Task<WorkflowVersion> CreateVersionAsync(WorkflowDefinitionV2 workflow, Guid userId,
        string changeDescription = "", CancellationToken ct = default);

    /// <summary>
    /// Get all versions of a workflow.
    /// </summary>
    Task<List<WorkflowVersion>> GetVersionsAsync(Guid workflowId, CancellationToken ct = default);

    /// <summary>
    /// Get a specific version.
    /// </summary>
    Task<WorkflowVersion?> GetVersionAsync(Guid workflowId, int version, CancellationToken ct = default);

    /// <summary>
    /// Rollback to a specific version.
    /// </summary>
    Task<WorkflowDefinitionV2> RollbackAsync(Guid workflowId, int version, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Compare two versions.
    /// </summary>
    Task<VersionDiff> CompareVersionsAsync(Guid workflowId, int fromVersion, int toVersion, CancellationToken ct = default);
}

/// <summary>
/// Difference between two workflow versions.
/// </summary>
public class VersionDiff
{
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
    public List<string> Changes { get; set; } = new();
    public string FromJson { get; set; } = string.Empty;
    public string ToJson { get; set; } = string.Empty;
}

/// <summary>
/// Service for workflow import/export.
/// </summary>
public interface IWorkflowExportService
{
    /// <summary>
    /// Export workflows to a package.
    /// </summary>
    Task<WorkflowExport> ExportAsync(List<Guid> workflowIds, bool includeTemplates = true,
        CancellationToken ct = default);

    /// <summary>
    /// Import workflows from a package.
    /// </summary>
    Task<ImportResult> ImportAsync(WorkflowExport package, ImportOptions options,
        Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Validate an import package.
    /// </summary>
    Task<List<string>> ValidateImportAsync(WorkflowExport package, CancellationToken ct = default);
}

/// <summary>
/// Import options.
/// </summary>
public class ImportOptions
{
    /// <summary>
    /// Whether to overwrite existing workflows with same ID.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// Whether to generate new IDs for imported workflows.
    /// </summary>
    public bool GenerateNewIds { get; set; } = true;

    /// <summary>
    /// Whether to import email templates.
    /// </summary>
    public bool ImportTemplates { get; set; } = true;

    /// <summary>
    /// Prefix to add to imported workflow names.
    /// </summary>
    public string? NamePrefix { get; set; }
}

/// <summary>
/// Result of an import operation.
/// </summary>
public class ImportResult
{
    public bool Success { get; set; }
    public int WorkflowsImported { get; set; }
    public int TemplatesImported { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public Dictionary<Guid, Guid> IdMappings { get; set; } = new();
}
