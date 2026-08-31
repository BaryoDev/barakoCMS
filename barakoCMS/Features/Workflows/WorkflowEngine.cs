using System.Diagnostics;
using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Marten;

namespace barakoCMS.Features.Workflows;

internal class WorkflowEngine : IWorkflowEngine
{
    private readonly IDocumentSession _session;
    private readonly IEnumerable<IWorkflowAction> _actions;
    private readonly ITemplateVariableExtractor _variableExtractor;
    private readonly IWorkflowDebugger _debugger;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(
        IDocumentSession session,
        IEnumerable<IWorkflowAction> actions,
        ITemplateVariableExtractor variableExtractor,
        IWorkflowDebugger debugger,
        ILogger<WorkflowEngine> logger)
    {
        _session = session;
        _actions = actions;
        _variableExtractor = variableExtractor;
        _debugger = debugger;
        _logger = logger;
    }

    public async Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct)
    {
        // Fault isolation: this method must never throw. It runs inside the async projection
        // daemon, where an unhandled exception stops the projection and silently halts ALL
        // workflows system-wide. Recovery from that state is documented in docs/operating-workflows.md
        // and is expensive, because a rebuild re-runs every action for every event ever stored.
        IReadOnlyList<WorkflowDefinition> workflows;
        try
        {
            workflows = await _session.Query<WorkflowDefinition>()
                .Where(w => w.TriggerContentType == contentType && w.TriggerEvent == eventType)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load workflows for {ContentType}/{EventType}", contentType, eventType);
            return;
        }

        foreach (var workflow in workflows)
        {
            try
            {
                if (MatchesConditions(workflow, content))
                {
                    await ExecuteActionsAsync(workflow, content, ct);
                }
            }
            catch (Exception ex)
            {
                // One workflow failing must not affect the others or stall the daemon.
                _logger.LogError(ex, "Workflow '{WorkflowName}' ({WorkflowId}) failed to execute", workflow.Name, workflow.Id);
            }
        }
    }

    private bool MatchesConditions(WorkflowDefinition workflow, barakoCMS.Models.Content content)
    {
        foreach (var condition in workflow.Conditions)
        {
            if (content.Data.TryGetValue(condition.Key, out var value))
            {
                if (value?.ToString() != condition.Value)
                {
                    return false;
                }
            }
            else if (condition.Key == "Status" && content.Status.ToString() != condition.Value)
            {
                return false;
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private async Task ExecuteActionsAsync(WorkflowDefinition workflow, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var run = _debugger.StartExecution(workflow.Id, content.Id);
        var overallTimer = Stopwatch.StartNew();

        foreach (var action in workflow.Actions)
        {
            var handler = _actions.FirstOrDefault(a => a.Type == action.Type);
            if (handler == null)
            {
                _logger.LogWarning("Unknown workflow action type '{ActionType}' in workflow '{WorkflowName}'. Skipping.", action.Type, workflow.Name);
                _debugger.LogActionFailure(run, action.Type, Stopwatch.StartNew(),
                    $"No handler is registered for action type '{action.Type}'.", action.Parameters);
                continue;
            }

            var resolvedParams = new Dictionary<string, string>(action.Parameters.Count);
            var timer = _debugger.StartAction(run, action.Type);

            try
            {
                // Resolve {{...}} template variables against the content BEFORE executing, so live
                // runs behave like the dry-run preview (previously only dry-run resolved them).
                foreach (var param in action.Parameters)
                {
                    resolvedParams[param.Key] = _variableExtractor.ResolveVariables(param.Value, content);
                }

                _logger.LogInformation("Executing workflow action '{ActionType}' for workflow '{WorkflowName}'", action.Type, workflow.Name);
                var result = await handler.RunAsync(resolvedParams, content, ct);

                if (result.Succeeded)
                {
                    _debugger.LogActionSuccess(run, action.Type, timer, resolvedParams);
                }
                else
                {
                    _debugger.LogActionFailure(run, action.Type, timer, result.Error ?? "The action reported failure without a reason.", resolvedParams);
                }
            }
            catch (Exception ex)
            {
                // Isolate per-action failures: a bad webhook/email must not prevent the remaining
                // actions in this workflow from running. An action that throws is a failed action,
                // which is what the run record has to say.
                _debugger.LogActionFailure(run, action.Type, timer, ex, resolvedParams);
                _logger.LogError(ex, "Workflow action '{ActionType}' in workflow '{WorkflowName}' failed", action.Type, workflow.Name);
            }
        }

        try
        {
            await _debugger.CompleteExecutionAsync(run, overallTimer, ct);
        }
        catch (Exception ex)
        {
            // The actions already ran. Failing to write the record must not be reported as the
            // workflow failing, and must not reach the daemon.
            _logger.LogError(ex, "Could not record the run of workflow '{WorkflowName}' ({WorkflowId})", workflow.Name, workflow.Id);
        }
    }
}
