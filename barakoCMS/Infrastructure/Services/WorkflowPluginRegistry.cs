using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Provides discovery and cataloging of workflow action plugins.
/// Implements automatic discovery of all registered <see cref="IWorkflowAction"/> implementations
/// and extracts their metadata via <see cref="WorkflowActionMetadataAttribute"/>.
/// </summary>
public interface IWorkflowPluginRegistry
{
    /// <summary>
    /// Retrieves metadata for all registered workflow action plugins.
    /// </summary>
    /// <returns>
    /// A read-only collection of <see cref="WorkflowActionMetadata"/> 
    /// containing information about each registered action plugin.
    /// </returns>
    /// <remarks>
    /// This method returns metadata extracted from <see cref="WorkflowActionMetadataAttribute"/>
    /// decorations on <see cref="IWorkflowAction"/> implementations. The returned collection
    /// provides a defensive copy to prevent external modification of internal state.
    /// </remarks>
    IReadOnlyList<WorkflowActionMetadata> GetAllActions();

    /// <summary>
    /// Retrieves metadata for a specific workflow action type.
    /// </summary>
    /// <param name="actionType">
    /// The type identifier of the workflow action (e.g., "Email", "SMS").
    /// This should match the <see cref="IWorkflowAction.Type"/> property.
    /// </param>
    /// <returns>
    /// The <see cref="WorkflowActionMetadata"/> for the specified action type,
    /// or <c>null</c> if the action type is not registered.
    /// </returns>
    WorkflowActionMetadata? GetActionMetadata(string actionType);

    /// <summary>
    /// Determines whether a workflow action type is registered in the system.
    /// </summary>
    /// <param name="actionType">
    /// The type identifier to check (e.g., "Email", "SMS", "Webhook").
    /// </param>
    /// <returns>
    /// <c>true</c> if the action type is registered and available for use;
    /// otherwise, <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This method is useful for validating workflow definitions before execution
    /// to ensure all referenced action types are available.
    /// </remarks>
    bool IsActionRegistered(string actionType);
}

/// <summary>
/// Registry for discovering and cataloging workflow action plugins.
/// </summary>
public class WorkflowPluginRegistry : IWorkflowPluginRegistry
{
    private readonly List<WorkflowActionMetadata> _metadata;
    private readonly Dictionary<string, WorkflowActionMetadata> _metadataByType;

    public WorkflowPluginRegistry(IEnumerable<IWorkflowAction> actions)
    {
        _metadata = new List<WorkflowActionMetadata>();
        _metadataByType = new Dictionary<string, WorkflowActionMetadata>();

        // Discover all actions and extract metadata
        foreach (var action in actions)
        {
            var actionType = action.GetType();
            var metadataAttr = actionType.GetCustomAttributes(typeof(WorkflowActionMetadataAttribute), false)
                .FirstOrDefault() as WorkflowActionMetadataAttribute;

            var metadata = new WorkflowActionMetadata
            {
                Type = action.Type,
                Description = metadataAttr?.Description ?? $"{action.Type} action",
                RequiredParameters = metadataAttr?.RequiredParameters?.ToList() ?? new List<string>(),
                ExampleConfiguration = metadataAttr?.ExampleJson ?? "{}"
            };

            _metadata.Add(metadata);
            _metadataByType[action.Type] = metadata;
        }
    }

    public IReadOnlyList<WorkflowActionMetadata> GetAllActions()
    {
        return _metadata.AsReadOnly();
    }

    public WorkflowActionMetadata? GetActionMetadata(string actionType)
    {
        return _metadataByType.GetValueOrDefault(actionType);
    }

    public bool IsActionRegistered(string actionType)
    {
        return _metadataByType.ContainsKey(actionType);
    }
}
