using barakoCMS.Models;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Marten;

namespace barakoCMS.Features.Workflows;

public class WorkflowEngine : IWorkflowEngine
{
    private readonly IDocumentSession _session;
    private readonly IEnumerable<IWorkflowAction> _actions;
    private readonly ITemplateVariableExtractor _variableExtractor;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(IDocumentSession session, IEnumerable<IWorkflowAction> actions, ITemplateVariableExtractor variableExtractor, ILogger<WorkflowEngine> logger)
    {
        _session = session;
        _actions = actions;
        _variableExtractor = variableExtractor;
        _logger = logger;
    }

    public async Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct)
    {
        // Fault isolation: this method must never throw. It runs inside the async projection
        // daemon, where an unhandled exception stops the projection and silently halts ALL
        // workflows system-wide until a manual rebuild.
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
        foreach (var action in workflow.Actions)
        {
            var handler = _actions.FirstOrDefault(a => a.Type == action.Type);
            if (handler == null)
            {
                _logger.LogWarning("Unknown workflow action type '{ActionType}' in workflow '{WorkflowName}'. Skipping.", action.Type, workflow.Name);
                continue;
            }

            try
            {
                // Resolve {{...}} template variables against the content BEFORE executing, so live
                // runs behave like the dry-run preview (previously only dry-run resolved them).
                var resolvedParams = new Dictionary<string, string>(action.Parameters.Count);
                foreach (var param in action.Parameters)
                {
                    resolvedParams[param.Key] = _variableExtractor.ResolveVariables(param.Value, content);
                }

                _logger.LogInformation("Executing workflow action '{ActionType}' for workflow '{WorkflowName}'", action.Type, workflow.Name);
                await handler.ExecuteAsync(resolvedParams, content, ct);
            }
            catch (Exception ex)
            {
                // Isolate per-action failures: a bad webhook/email must not prevent the remaining
                // actions in this workflow from running.
                _logger.LogError(ex, "Workflow action '{ActionType}' in workflow '{WorkflowName}' failed", action.Type, workflow.Name);
            }
        }
    }
}
