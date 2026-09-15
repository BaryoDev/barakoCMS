using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Interface for workflow debugging and execution logging.
/// </summary>
public interface IWorkflowDebugger
{
    /// <summary>
    /// Start a new workflow execution log.
    /// </summary>
    WorkflowExecutionLog StartExecution(Guid workflowId, Guid contentId, bool isDryRun = false);

    /// <summary>
    /// Log the start of an action execution.
    /// </summary>
    Stopwatch StartAction(WorkflowExecutionLog log, string actionType);

    /// <summary>
    /// Log a successful action execution.
    /// </summary>
    void LogActionSuccess(WorkflowExecutionLog log, string actionType, Stopwatch timer, Dictionary<string, string> resolvedParams);

    /// <summary>
    /// Log a failed action execution.
    /// </summary>
    void LogActionFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, Exception ex, Dictionary<string, string> resolvedParams);

    /// <summary>
    /// Log a failed action execution that reported its own reason rather than throwing.
    /// </summary>
    /// <remarks>
    /// Default implementation so an existing implementor still compiles. It records the same entry
    /// the exception overload does, minus the log line, which needs the concrete logger.
    /// </remarks>
    void LogActionFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, string error, Dictionary<string, string> resolvedParams)
    {
        timer.Stop();

        log.Actions.Add(new ActionExecutionLog
        {
            ActionType = actionType,
            Success = false,
            ErrorMessage = error,
            ResolvedParameters = WorkflowDebugger.RecordableParameters(resolvedParams),
            Duration = timer.Elapsed
        });

        log.Success = false;
    }

    /// <summary>
    /// Complete the workflow execution log and save it.
    /// </summary>
    Task CompleteExecutionAsync(WorkflowExecutionLog log, Stopwatch overallTimer, CancellationToken ct = default);

    /// <summary>
    /// Get execution history for a workflow.
    /// </summary>
    Task<List<WorkflowExecutionLog>> GetExecutionHistoryAsync(Guid workflowId, int limit = 20, CancellationToken ct = default);
}

/// <summary>
/// Provides debugging capabilities for workflow execution.
/// </summary>
public class WorkflowDebugger : IWorkflowDebugger
{
    private readonly IDocumentSession _session;
    private readonly ILogger<WorkflowDebugger> _logger;

    public WorkflowDebugger(IDocumentSession session, ILogger<WorkflowDebugger> logger)
    {
        _session = session;
        _logger = logger;
    }

    public WorkflowExecutionLog StartExecution(Guid workflowId, Guid contentId, bool isDryRun = false)
    {
        var log = new WorkflowExecutionLog
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflowId,
            ContentId = contentId,
            ExecutedAt = DateTime.UtcNow,
            IsDryRun = isDryRun,
            Redacted = true,
            Success = true // Assume success unless proven otherwise
        };

        _logger.LogInformation(
            "Starting workflow execution: WorkflowId={WorkflowId}, ContentId={ContentId}, DryRun={DryRun}",
            workflowId, contentId, isDryRun);

        return log;
    }

    public Stopwatch StartAction(WorkflowExecutionLog log, string actionType)
    {
        _logger.LogInformation("Starting action: {ActionType} (DryRun={DryRun})", actionType, log.IsDryRun);
        return Stopwatch.StartNew();
    }

    public void LogActionSuccess(WorkflowExecutionLog log, string actionType, Stopwatch timer, Dictionary<string, string> resolvedParams)
    {
        timer.Stop();

        var actionLog = new ActionExecutionLog
        {
            ActionType = actionType,
            Success = true,
            ResolvedParameters = WorkflowDebugger.RecordableParameters(resolvedParams),
            Duration = timer.Elapsed
        };

        log.Actions.Add(actionLog);

        _logger.LogInformation(
            "Action completed successfully: {ActionType} in {Duration}ms",
            actionType, timer.ElapsedMilliseconds);
    }

    public void LogActionFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, Exception ex, Dictionary<string, string> resolvedParams)
    {
        // The type only. An exception message can carry what the action was sending, a recipient
        // or a provider's error body with the credential in it, and this record is served over the
        // API. The message stays in the log line below.
        RecordFailure(log, actionType, timer, ex.GetType().Name, resolvedParams);

        _logger.LogError(ex,
            "Action failed: {ActionType} after {Duration}ms",
            actionType, timer.ElapsedMilliseconds);
    }

    public void LogActionFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, string error, Dictionary<string, string> resolvedParams)
    {
        RecordFailure(log, actionType, timer, error, resolvedParams);

        _logger.LogError(
            "Action failed: {ActionType} after {Duration}ms: {Error}",
            actionType, timer.ElapsedMilliseconds, error);
    }

    private static void RecordFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, string error, Dictionary<string, string> resolvedParams)
    {
        timer.Stop();

        var actionLog = new ActionExecutionLog
        {
            ActionType = actionType,
            Success = false,
            ErrorMessage = error,
            ResolvedParameters = WorkflowDebugger.RecordableParameters(resolvedParams),
            Duration = timer.Elapsed
        };

        log.Actions.Add(actionLog);
        log.Success = false; // Mark overall execution as failed
    }

    public async Task CompleteExecutionAsync(WorkflowExecutionLog log, Stopwatch overallTimer, CancellationToken ct = default)
    {
        overallTimer.Stop();
        log.Duration = overallTimer.Elapsed;

        // Save execution log to database
        _session.Store(log);
        await _session.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Workflow execution completed: WorkflowId={WorkflowId}, Success={Success}, Duration={Duration}ms, Actions={ActionCount}",
            log.WorkflowId, log.Success, overallTimer.ElapsedMilliseconds, log.Actions.Count);
    }

    public async Task<List<WorkflowExecutionLog>> GetExecutionHistoryAsync(Guid workflowId, int limit = 20, CancellationToken ct = default)
    {
        var logs = await _session.Query<WorkflowExecutionLog>()
            .Where(log => log.WorkflowId == workflowId)
            .OrderByDescending(log => log.ExecutedAt)
            .Take(limit)
            .ToListAsync(ct);

        return logs.Select(RedactForReading).ToList();
    }

    /// <summary>What a log line shows in place of a parameter value that is not on the allowlist.</summary>
    internal const string RedactedValue = "[redacted]";

    internal const string UnredactedErrorMessage =
        "Recorded before execution logs were redacted. The server log has the detail.";

    /// <summary>
    /// Parameter names whose value is structural (an identifier, a content type, a status) rather
    /// than something an action sends.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a denylist. Parameters are free-form and resolved against the content, so a
    /// recipient, a URL with a token in its query, or a credential under a name nobody thought of
    /// all arrive looking like any other parameter. Anything not named here keeps its key and loses
    /// its value; a credential-named key is dropped outright, the same as everywhere else.
    /// </remarks>
    private static readonly HashSet<string> RecordableParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContentType", "Status", "Field", "TargetId", "Request", "TriggerEvent", "RunId", "WorkflowId", "Attempt",
    };

    internal static Dictionary<string, string> RecordableParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var recorded = barakoCMS.Features.Workflows.Actions.WebhookSigning.WithoutSecret(parameters);
        foreach (var key in recorded.Keys.ToList())
        {
            if (!RecordableParameterNames.Contains(key)) recorded[key] = RedactedValue;
        }

        return recorded;
    }

    /// <summary>
    /// Redacts a stored log again on the way out, so a record written before redaction existed does
    /// not keep serving what it captured.
    /// </summary>
    private static WorkflowExecutionLog RedactForReading(WorkflowExecutionLog log)
    {
        RedactActions(log);
        return log;
    }

    /// <summary>
    /// Applies the redaction a log gets on the way out to the log itself, and marks it redacted, so
    /// the stored row stops holding what it captured. What <c>WorkflowExecutionLogRedactionService</c>
    /// writes back.
    /// </summary>
    /// <returns>False when the log was already redacted and nothing was changed.</returns>
    internal static bool RedactStored(WorkflowExecutionLog log)
    {
        if (log.Redacted) return false;

        RedactActions(log);
        log.Redacted = true;
        return true;
    }

    private static void RedactActions(WorkflowExecutionLog log)
    {
        foreach (var action in log.Actions)
        {
            action.ResolvedParameters = RecordableParameters(action.ResolvedParameters);

            // An older record may hold a raw exception message, and nothing on it says which failures
            // were exceptions, so every error on it is replaced.
            if (!log.Redacted && action.ErrorMessage is not null)
            {
                action.ErrorMessage = UnredactedErrorMessage;
            }
        }
    }

}
