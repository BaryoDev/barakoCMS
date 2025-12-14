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
            ResolvedParameters = new Dictionary<string, string>(resolvedParams),
            Duration = timer.Elapsed
        };

        log.Actions.Add(actionLog);

        _logger.LogInformation(
            "Action completed successfully: {ActionType} in {Duration}ms",
            actionType, timer.ElapsedMilliseconds);
    }

    public void LogActionFailure(WorkflowExecutionLog log, string actionType, Stopwatch timer, Exception ex, Dictionary<string, string> resolvedParams)
    {
        timer.Stop();

        var actionLog = new ActionExecutionLog
        {
            ActionType = actionType,
            Success = false,
            ErrorMessage = ex.Message,
            ResolvedParameters = new Dictionary<string, string>(resolvedParams),
            Duration = timer.Elapsed
        };

        log.Actions.Add(actionLog);
        log.Success = false; // Mark overall execution as failed

        _logger.LogError(ex,
            "Action failed: {ActionType} after {Duration}ms",
            actionType, timer.ElapsedMilliseconds);
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

        return logs.ToList();
    }

}
