using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>
/// What a handler is told when a run ends Failed or PartiallyFailed: the run as saved, and the role
/// its workflow names in <c>NotifyRoleOnFailure</c> (null when it names none).
/// </summary>
public sealed record WorkflowRunFailure(WorkflowRun Run, string? NotifyRole);

/// <summary>
/// Lets a module react when a workflow run fails, for example to tell the role the workflow names.
/// Core calls every registered handler once the failed outcome is saved. Core has no handler of its
/// own, so with none registered nothing happens beyond the run's own record.
/// </summary>
/// <remarks>
/// A handler resolves in the run's tenant scope, so it can take that tenant's <c>IDocumentSession</c>.
/// A handler that throws is logged and skipped; it cannot change the run or stop the next handler.
/// The same run can be reported again after a retry of the record, so a handler writes idempotently.
/// </remarks>
public interface IWorkflowRunFailureHandler
{
    Task OnRunFailedAsync(WorkflowRunFailure failure, CancellationToken ct);
}
