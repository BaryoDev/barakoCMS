using System.Text.Json;
using barakoCMS.Features.WorkflowsV2.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Service for workflow import/export.
/// </summary>
public class WorkflowExportService : IWorkflowExportService
{
    private readonly IDocumentSession _session;
    private readonly IEmailTemplateService _templateService;
    private readonly ICredentialService _credentialService;
    private readonly IWorkflowVersionService _versionService;
    private readonly ILogger<WorkflowExportService> _logger;

    public WorkflowExportService(
        IDocumentSession session,
        IEmailTemplateService templateService,
        ICredentialService credentialService,
        IWorkflowVersionService versionService,
        ILogger<WorkflowExportService> logger)
    {
        _session = session;
        _templateService = templateService;
        _credentialService = credentialService;
        _versionService = versionService;
        _logger = logger;
    }

    public async Task<WorkflowExport> ExportAsync(
        List<Guid> workflowIds,
        bool includeTemplates = true,
        CancellationToken ct = default)
    {
        var export = new WorkflowExport
        {
            ExportedAt = DateTime.UtcNow
        };

        // Load workflows
        foreach (var id in workflowIds)
        {
            var workflow = await _session.LoadAsync<WorkflowDefinitionV2>(id, ct);
            if (workflow != null)
            {
                export.Workflows.Add(workflow);
            }
        }

        if (includeTemplates)
        {
            // Find used templates
            var usedTemplates = new HashSet<string>();

            foreach (var workflow in export.Workflows)
            {
                foreach (var action in workflow.Actions)
                {
                    if (action.Type == "SendEmail" &&
                        action.Config.TryGetValue("template", out var template))
                    {
                        usedTemplates.Add(template?.ToString() ?? "");
                    }
                }
            }

            // Load templates
            foreach (var templateName in usedTemplates.Where(t => !string.IsNullOrEmpty(t)))
            {
                var template = await _templateService.GetTemplateAsync(templateName);
                if (template != null)
                {
                    export.EmailTemplates.Add(new EmailTemplateExport
                    {
                        Name = template.Name,
                        Subject = template.Subject,
                        HtmlBody = template.HtmlBody,
                        TextBody = template.TextBody,
                        Variables = template.Variables
                    });
                }
            }
        }

        // Export credential metadata (no secrets)
        var credentials = await _credentialService.ListCredentialsAsync(ct);
        foreach (var cred in credentials)
        {
            export.Credentials.Add(new CredentialExport
            {
                Name = cred.Name,
                Type = cred.Type.ToString(),
                Description = cred.Description,
                RequiredScopes = cred.Scopes
            });
        }

        _logger.LogInformation("Exported {WorkflowCount} workflows, {TemplateCount} templates",
            export.Workflows.Count, export.EmailTemplates.Count);

        return export;
    }

    public async Task<ImportResult> ImportAsync(
        WorkflowExport package,
        ImportOptions options,
        Guid userId,
        CancellationToken ct = default)
    {
        var result = new ImportResult { Success = true };

        // Validate first
        var validationErrors = await ValidateImportAsync(package, ct);
        if (validationErrors.Count > 0)
        {
            result.Success = false;
            result.Errors = validationErrors;
            return result;
        }

        // Import templates first
        if (options.ImportTemplates)
        {
            foreach (var templateExport in package.EmailTemplates)
            {
                try
                {
                    var template = new EmailTemplate
                    {
                        Name = templateExport.Name,
                        Subject = templateExport.Subject,
                        HtmlBody = templateExport.HtmlBody,
                        TextBody = templateExport.TextBody,
                        Variables = templateExport.Variables,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    await _templateService.SaveTemplateAsync(template);
                    result.TemplatesImported++;
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Failed to import template '{templateExport.Name}': {ex.Message}");
                }
            }
        }

        // Import workflows
        foreach (var workflow in package.Workflows)
        {
            try
            {
                var originalId = workflow.Id;
                Guid newId;

                if (options.GenerateNewIds)
                {
                    newId = Guid.NewGuid();
                    workflow.Id = newId;
                }
                else
                {
                    newId = workflow.Id;

                    // Check if exists
                    var existing = await _session.LoadAsync<WorkflowDefinitionV2>(workflow.Id, ct);

                    if (existing != null && !options.OverwriteExisting)
                    {
                        result.Warnings.Add($"Workflow '{workflow.Name}' already exists and overwrite is disabled");
                        continue;
                    }
                }

                // Apply name prefix
                if (!string.IsNullOrEmpty(options.NamePrefix))
                {
                    workflow.Name = $"{options.NamePrefix}{workflow.Name}";
                }

                // Reset metadata
                workflow.Version = 1;
                workflow.CreatedAt = DateTime.UtcNow;
                workflow.UpdatedAt = DateTime.UtcNow;
                workflow.CreatedBy = userId;
                workflow.UpdatedBy = userId;

                // Save workflow
                _session.Store(workflow);

                // Create initial version
                await _versionService.CreateVersionAsync(workflow, userId, "Imported from package", ct);

                result.IdMappings[originalId] = newId;
                result.WorkflowsImported++;

                _logger.LogInformation("Imported workflow '{Name}' with ID {Id}", workflow.Name, newId);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to import workflow '{workflow.Name}': {ex.Message}");
                result.Success = false;
            }
        }

        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Import completed: {WorkflowCount} workflows, {TemplateCount} templates, {ErrorCount} errors",
            result.WorkflowsImported, result.TemplatesImported, result.Errors.Count);

        return result;
    }

    public async Task<List<string>> ValidateImportAsync(WorkflowExport package, CancellationToken ct = default)
    {
        var errors = new List<string>();

        if (package.FormatVersion != "1.0")
        {
            errors.Add($"Unsupported format version: {package.FormatVersion}");
        }

        foreach (var workflow in package.Workflows)
        {
            if (string.IsNullOrEmpty(workflow.Name))
            {
                errors.Add($"Workflow with ID {workflow.Id} has no name");
            }

            if (string.IsNullOrEmpty(workflow.Trigger.ContentType))
            {
                errors.Add($"Workflow '{workflow.Name}' has no trigger content type");
            }

            if (string.IsNullOrEmpty(workflow.Trigger.Event))
            {
                errors.Add($"Workflow '{workflow.Name}' has no trigger event");
            }

            // Validate actions
            foreach (var action in workflow.Actions)
            {
                if (string.IsNullOrEmpty(action.Type))
                {
                    errors.Add($"Workflow '{workflow.Name}' has action with no type");
                }
            }
        }

        // Check template references
        var availableTemplates = (await _templateService.ListTemplatesAsync())
            .Select(t => t.Name.ToLowerInvariant())
            .ToHashSet();

        var exportedTemplates = package.EmailTemplates
            .Select(t => t.Name.ToLowerInvariant())
            .ToHashSet();

        foreach (var workflow in package.Workflows)
        {
            foreach (var action in workflow.Actions.Where(a => a.Type == "SendEmail"))
            {
                if (action.Config.TryGetValue("template", out var template))
                {
                    var templateName = template?.ToString()?.ToLowerInvariant() ?? "";
                    if (!string.IsNullOrEmpty(templateName) &&
                        !availableTemplates.Contains(templateName) &&
                        !exportedTemplates.Contains(templateName))
                    {
                        errors.Add($"Workflow '{workflow.Name}' references missing template '{template}'");
                    }
                }
            }
        }

        return errors;
    }
}
