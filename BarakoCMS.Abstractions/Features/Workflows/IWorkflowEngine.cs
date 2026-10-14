using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>Runs a workflow's actions in the caller, without the queue.</summary>
/// <remarks>
/// Nothing in the host calls this: content events queue runs, and the background runner executes
/// them with retries, leases and a record per attempt. This path has none of that.
/// </remarks>
[Obsolete("Nothing in the host runs workflows through IWorkflowEngine. Workflows are queued from content "
    + "events and run by the background runner; a module starts its own with IWorkflowTriggers. "
    + "IWorkflowEngine is removed in barakoCMS 6.0.")]
public interface IWorkflowEngine
{
    Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct);
}
