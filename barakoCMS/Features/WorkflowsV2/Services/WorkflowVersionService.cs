using System.Text.Json;
using barakoCMS.Features.WorkflowsV2.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Service for workflow version management.
/// </summary>
public class WorkflowVersionService : IWorkflowVersionService
{
    private readonly IDocumentSession _session;
    private readonly ILogger<WorkflowVersionService> _logger;

    public WorkflowVersionService(IDocumentSession session, ILogger<WorkflowVersionService> logger)
    {
        _session = session;
        _logger = logger;
    }

    public async Task<WorkflowVersion> CreateVersionAsync(
        WorkflowDefinitionV2 workflow,
        Guid userId,
        string changeDescription = "",
        CancellationToken ct = default)
    {
        // Deactivate previous active version
        var previousVersions = await _session.Query<WorkflowVersion>()
            .Where(v => v.WorkflowId == workflow.Id && v.IsActive)
            .ToListAsync(ct);

        foreach (var prev in previousVersions)
        {
            prev.IsActive = false;
            _session.Store(prev);
        }

        // Create new version
        var version = WorkflowVersion.FromDefinition(workflow, userId, changeDescription);

        _session.Store(version);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Created version {Version} for workflow {WorkflowId}",
            version.Version, workflow.Id);

        return version;
    }

    public async Task<List<WorkflowVersion>> GetVersionsAsync(Guid workflowId, CancellationToken ct = default)
    {
        var versions = await _session.Query<WorkflowVersion>()
            .Where(v => v.WorkflowId == workflowId)
            .OrderByDescending(v => v.Version)
            .ToListAsync(ct);
        return versions.ToList();
    }

    public async Task<WorkflowVersion?> GetVersionAsync(Guid workflowId, int version, CancellationToken ct = default)
    {
        return await _session.Query<WorkflowVersion>()
            .FirstOrDefaultAsync(v => v.WorkflowId == workflowId && v.Version == version, ct);
    }

    public async Task<WorkflowDefinitionV2> RollbackAsync(
        Guid workflowId,
        int version,
        Guid userId,
        CancellationToken ct = default)
    {
        var targetVersion = await GetVersionAsync(workflowId, version, ct);

        if (targetVersion == null)
        {
            throw new ArgumentException($"Version {version} not found for workflow {workflowId}");
        }

        var workflow = targetVersion.GetDefinition();

        if (workflow == null)
        {
            throw new InvalidOperationException("Failed to deserialize workflow definition from version");
        }

        // Get current workflow to determine next version number
        var currentWorkflow = await _session.LoadAsync<WorkflowDefinitionV2>(workflowId, ct);
        var newVersionNumber = (currentWorkflow?.Version ?? 0) + 1;

        workflow.Version = newVersionNumber;
        workflow.UpdatedAt = DateTime.UtcNow;
        workflow.UpdatedBy = userId;

        // Save the rolled-back workflow
        _session.Store(workflow);

        // Create a new version entry for the rollback
        await CreateVersionAsync(workflow, userId, $"Rolled back to version {version}", ct);

        await _session.SaveChangesAsync(ct);

        _logger.LogInformation("Rolled back workflow {WorkflowId} to version {Version}, new version is {NewVersion}",
            workflowId, version, newVersionNumber);

        return workflow;
    }

    public async Task<VersionDiff> CompareVersionsAsync(
        Guid workflowId,
        int fromVersion,
        int toVersion,
        CancellationToken ct = default)
    {
        var from = await GetVersionAsync(workflowId, fromVersion, ct);
        var to = await GetVersionAsync(workflowId, toVersion, ct);

        if (from == null || to == null)
        {
            throw new ArgumentException("One or both versions not found");
        }

        var fromDef = from.GetDefinition();
        var toDef = to.GetDefinition();

        var changes = new List<string>();

        if (fromDef != null && toDef != null)
        {
            // Compare basic properties
            if (fromDef.Name != toDef.Name)
                changes.Add($"Name changed from '{fromDef.Name}' to '{toDef.Name}'");

            if (fromDef.Description != toDef.Description)
                changes.Add("Description changed");

            if (fromDef.Enabled != toDef.Enabled)
                changes.Add($"Enabled changed from {fromDef.Enabled} to {toDef.Enabled}");

            if (fromDef.Priority != toDef.Priority)
                changes.Add($"Priority changed from {fromDef.Priority} to {toDef.Priority}");

            // Compare trigger
            if (fromDef.Trigger.ContentType != toDef.Trigger.ContentType)
                changes.Add($"Trigger content type changed from '{fromDef.Trigger.ContentType}' to '{toDef.Trigger.ContentType}'");

            if (fromDef.Trigger.Event != toDef.Trigger.Event)
                changes.Add($"Trigger event changed from '{fromDef.Trigger.Event}' to '{toDef.Trigger.Event}'");

            // Compare actions
            if (fromDef.Actions.Count != toDef.Actions.Count)
            {
                changes.Add($"Number of actions changed from {fromDef.Actions.Count} to {toDef.Actions.Count}");
            }
            else
            {
                for (int i = 0; i < fromDef.Actions.Count; i++)
                {
                    var fromAction = fromDef.Actions[i];
                    var toAction = toDef.Actions[i];

                    if (fromAction.Type != toAction.Type)
                        changes.Add($"Action {i + 1} type changed from '{fromAction.Type}' to '{toAction.Type}'");
                }
            }
        }

        return new VersionDiff
        {
            FromVersion = fromVersion,
            ToVersion = toVersion,
            Changes = changes,
            FromJson = from.DefinitionJson,
            ToJson = to.DefinitionJson
        };
    }
}
