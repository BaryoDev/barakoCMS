using barakoCMS.Models;
using Microsoft.Extensions.Logging;
using Marten;

namespace barakoCMS.Features.Workflows;

public class WorkflowEngine : IWorkflowEngine
{
    private readonly IDocumentSession _session;
    private readonly IEnumerable<IWorkflowAction> _actions;
    private readonly ILogger<WorkflowEngine> _logger;

    public WorkflowEngine(IDocumentSession session, IEnumerable<IWorkflowAction> actions, ILogger<WorkflowEngine> logger)
    {
        _session = session;
        _actions = actions;
        _logger = logger;
    }

    public async Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct)
    {
        // Find matching workflows
        var workflows = await _session.Query<WorkflowDefinition>()
            .Where(w => w.TriggerContentType == contentType && w.TriggerEvent == eventType)
            .ToListAsync(ct);

        foreach (var workflow in workflows)
        {
            if (MatchesConditions(workflow, content))
            {
                await ExecuteActionsAsync(workflow, content, ct);
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
            if (handler != null)
            {
                _logger.LogInformation("Executing workflow action '{ActionType}' for workflow '{WorkflowName}'", action.Type, workflow.Name);
                await handler.ExecuteAsync(action.Parameters, content, ct);
            }
            else
            {
                _logger.LogWarning("Unknown workflow action type '{ActionType}' in workflow '{WorkflowName}'. Skipping.", action.Type, workflow.Name);
            }
        }
    }
}
