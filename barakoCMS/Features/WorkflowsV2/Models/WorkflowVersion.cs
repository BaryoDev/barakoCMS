using System.Text.Json;

namespace barakoCMS.Features.WorkflowsV2.Models;

/// <summary>
/// Version history entry for a workflow definition.
/// </summary>
public class WorkflowVersion
{
    public Guid Id { get; set; }

    /// <summary>
    /// The workflow this version belongs to.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Version number.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Complete snapshot of the workflow at this version.
    /// </summary>
    public string DefinitionJson { get; set; } = string.Empty;

    /// <summary>
    /// Hash of the definition for change detection.
    /// </summary>
    public string DefinitionHash { get; set; } = string.Empty;

    /// <summary>
    /// Description of changes in this version.
    /// </summary>
    public string ChangeDescription { get; set; } = string.Empty;

    /// <summary>
    /// Who created this version.
    /// </summary>
    public Guid CreatedBy { get; set; }

    /// <summary>
    /// When this version was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether this is the currently active version.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether this version was ever published/enabled.
    /// </summary>
    public bool WasPublished { get; set; }

    /// <summary>
    /// Deserialize the workflow definition.
    /// </summary>
    public WorkflowDefinitionV2? GetDefinition()
    {
        if (string.IsNullOrEmpty(DefinitionJson))
            return null;

        return JsonSerializer.Deserialize<WorkflowDefinitionV2>(DefinitionJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    /// <summary>
    /// Create a version from a workflow definition.
    /// </summary>
    public static WorkflowVersion FromDefinition(WorkflowDefinitionV2 definition, Guid userId, string changeDescription = "")
    {
        var json = JsonSerializer.Serialize(definition, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        return new WorkflowVersion
        {
            Id = Guid.NewGuid(),
            WorkflowId = definition.Id,
            Version = definition.Version,
            DefinitionJson = json,
            DefinitionHash = ComputeHash(json),
            ChangeDescription = changeDescription,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            WasPublished = definition.Enabled
        };
    }

    private static string ComputeHash(string content)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }
}

/// <summary>
/// Workflow export/import format.
/// </summary>
public class WorkflowExport
{
    /// <summary>
    /// Export format version.
    /// </summary>
    public string FormatVersion { get; set; } = "1.0";

    /// <summary>
    /// When the export was created.
    /// </summary>
    public DateTime ExportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Source system identifier.
    /// </summary>
    public string SourceSystem { get; set; } = "BarakoCMS";

    /// <summary>
    /// Workflow definitions to export.
    /// </summary>
    public List<WorkflowDefinitionV2> Workflows { get; set; } = new();

    /// <summary>
    /// Email templates used by the workflows.
    /// </summary>
    public List<EmailTemplateExport> EmailTemplates { get; set; } = new();

    /// <summary>
    /// Action credentials (metadata only, not secrets).
    /// </summary>
    public List<CredentialExport> Credentials { get; set; } = new();
}

/// <summary>
/// Email template export format.
/// </summary>
public class EmailTemplateExport
{
    public string Name { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public string TextBody { get; set; } = string.Empty;
    public List<string> Variables { get; set; } = new();
}

/// <summary>
/// Credential metadata export (no secrets).
/// </summary>
public class CredentialExport
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> RequiredScopes { get; set; } = new();
}
