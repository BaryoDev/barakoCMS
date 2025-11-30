using barakoCMS.Models;
using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Features.Workflows;

public class WorkflowEngine
{
    private readonly IDocumentSession _session;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;

    public WorkflowEngine(IDocumentSession session, IEmailService emailService, ISmsService smsService)
    {
        _session = session;
        _emailService = emailService;
        _smsService = smsService;
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
            switch (action.Type)
            {
                case "Email":
                    await _emailService.SendEmailAsync(
                        action.Parameters.GetValueOrDefault("To", "admin@example.com"),
                        $"Workflow Triggered: {workflow.Name}",
                        $"Content {content.Id} triggered this workflow.",
                        ct);
                    break;
                case "SMS":
                    await _smsService.SendSmsAsync(
                        action.Parameters.GetValueOrDefault("To", "+1234567890"),
                        $"Workflow: {workflow.Name} triggered.",
                        ct);
                    break;
                // Add more actions here
            }
        }
    }
}
