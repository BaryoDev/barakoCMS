using System.Linq.Expressions;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>Which content types a workflow fires for, and the query that finds the workflows for an event.</summary>
/// <remarks>
/// Shared by the engine, the run queue, the validator and the response, for the reason
/// <see cref="WorkflowConditions"/> is shared: two readings of the same fields would disagree the
/// first time one of them changed.
/// </remarks>
internal static class WorkflowTriggers
{
    /// <summary>
    /// <see cref="WorkflowDefinition.TriggerContentType"/> followed by every entry in
    /// <see cref="WorkflowDefinition.TriggerContentTypes"/>, blanks dropped and each type once.
    /// </summary>
    /// <remarks>
    /// A document saved before the list existed deserialises with an empty list, so this is its
    /// single type and nothing else. No upcaster is needed for that.
    /// </remarks>
    internal static List<string> ContentTypes(WorkflowDefinition workflow)
    {
        var types = new List<string>();

        if (!string.IsNullOrWhiteSpace(workflow.TriggerContentType))
        {
            types.Add(workflow.TriggerContentType);
        }

        foreach (var type in workflow.TriggerContentTypes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(type) && !types.Contains(type, StringComparer.Ordinal))
            {
                types.Add(type);
            }
        }

        return types;
    }

    /// <summary>The workflows an event on <paramref name="contentType"/> fires.</summary>
    /// <remarks>
    /// Checks both fields rather than only the list, because a stored document written before the
    /// list existed has no list, and a document stored straight through a session need not fill it.
    /// </remarks>
    internal static Expression<Func<WorkflowDefinition, bool>> FiredBy(string contentType, string eventType) =>
        w => (w.TriggerContentType == contentType || w.TriggerContentTypes.Contains(contentType))
             && w.TriggerEvent == eventType;

    /// <summary>
    /// Stores the trigger in the shape every reader understands: the list holds every type, and the
    /// single field holds the first.
    /// </summary>
    internal static void Normalise(WorkflowDefinition workflow)
    {
        var types = ContentTypes(workflow);
        workflow.TriggerContentTypes = types;
        workflow.TriggerContentType = types.Count > 0 ? types[0] : string.Empty;
    }
}
