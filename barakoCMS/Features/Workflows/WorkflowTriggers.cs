using System.Linq.Expressions;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>Which content types and events a workflow fires for, and the query that finds the workflows for an event.</summary>
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
    internal static List<string> ContentTypes(WorkflowDefinition workflow) =>
        Merge(workflow.TriggerContentType, workflow.TriggerContentTypes);

    /// <summary>
    /// <see cref="WorkflowDefinition.TriggerEvent"/> followed by every entry in
    /// <see cref="WorkflowDefinition.TriggerEvents"/>, blanks dropped and each event once.
    /// </summary>
    internal static List<string> Events(WorkflowDefinition workflow) =>
        Merge(workflow.TriggerEvent, workflow.TriggerEvents);

    /// <summary>The workflows an event on <paramref name="contentType"/> fires.</summary>
    /// <remarks>
    /// Checks the single fields as well as the lists, because a stored document written before a
    /// list existed has no list, and a document stored straight through a session need not fill it.
    ///
    /// A filter, not a join, so a workflow comes back once however many of its list entries the
    /// event matches.
    /// </remarks>
    internal static Expression<Func<WorkflowDefinition, bool>> FiredBy(string contentType, string eventType) =>
        w => (w.TriggerContentType == contentType || w.TriggerContentTypes.Contains(contentType))
             && (w.TriggerEvent == eventType || w.TriggerEvents.Contains(eventType));

    /// <summary>
    /// Stores the trigger in the shape every reader understands: each list holds every value, and
    /// each single field holds the first.
    /// </summary>
    internal static void Normalise(WorkflowDefinition workflow)
    {
        var types = ContentTypes(workflow);
        workflow.TriggerContentTypes = types;
        workflow.TriggerContentType = types.Count > 0 ? types[0] : string.Empty;

        var events = Events(workflow);
        workflow.TriggerEvents = events;
        workflow.TriggerEvent = events.Count > 0 ? events[0] : string.Empty;
    }

    private static List<string> Merge(string? single, List<string>? listed)
    {
        var values = new List<string>();

        if (!string.IsNullOrWhiteSpace(single))
        {
            values.Add(single);
        }

        foreach (var value in listed ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }
}
