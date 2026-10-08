using System.Diagnostics;
using barakoCMS.Models;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Marten;

namespace barakoCMS.Features.Workflows;

internal class WorkflowEngine(
    IDocumentSession session,
    IEnumerable<IWorkflowAction> actions,
    ITemplateVariableExtractor variableExtractor,
    IWorkflowDebugger debugger,
    ISecretProtector protector,
    ILogger<WorkflowEngine> logger) : IWorkflowEngine
{
    public async Task ProcessEventAsync(string contentType, string eventType, barakoCMS.Models.Content content, CancellationToken ct)
    {
        // Fault isolation: this method must never throw. It runs inside the async projection
        // daemon, where an unhandled exception stops the projection and silently halts ALL
        // workflows system-wide. Recovery from that state is documented in docs/operating-workflows.md
        // and is expensive, because a rebuild re-runs every action for every event ever stored.
        IReadOnlyList<WorkflowDefinition> workflows;
        try
        {
            workflows = WorkflowTriggers.SwitchedOn(await session.Query<WorkflowDefinition>()
                .Where(WorkflowTriggers.FiredBy(contentType, eventType))
                .ToListAsync(ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load workflows for {ContentType}/{EventType}", contentType, eventType);
            return;
        }

        // A Deleted event means here what it means on the queued path: the entry is erased, an
        // action is told which entry went and nothing of what it held, and a workflow with
        // conditions does not fire, since its conditions read the data that is gone. Whatever the
        // caller passed, only the id and the content type go further.
        var erased = eventType == WorkflowEvents.Deleted;
        if (erased) content = new ErasedContent(content.Id, contentType);

        foreach (var workflow in workflows)
        {
            try
            {
                if (erased && workflow.Conditions is { Count: > 0 }) continue;

                if (MatchesConditions(workflow, content))
                {
                    await ExecuteActionsAsync(workflow, eventType, content, ct);
                }
            }
            catch (Exception ex)
            {
                // One workflow failing must not affect the others or stall the daemon.
                logger.LogError(ex, "Workflow '{WorkflowName}' ({WorkflowId}) failed to execute", workflow.Name, workflow.Id);
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

    private async Task ExecuteActionsAsync(WorkflowDefinition workflow, string eventType, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var run = debugger.StartExecution(workflow.Id, content.Id);
        var overallTimer = Stopwatch.StartNew();

        // Nothing is retried on this path, so any failure of an action set to halt is final and
        // the actions after it are recorded as not run.
        var halted = false;

        foreach (var action in workflow.Actions)
        {
            if (halted)
            {
                // Written to the record directly. The debugger's failure call logs an error saying
                // the action failed, and this one did not run.
                run.Actions.Add(new ActionExecutionLog
                {
                    ActionType = action.Type,
                    Success = false,
                    ErrorMessage = WorkflowRun.SkippedAfterHalt,
                    ResolvedParameters = WorkflowDebugger.RecordableParameters(action.Parameters),
                });
                logger.LogInformation(
                    "Workflow action '{ActionType}' in workflow '{WorkflowName}' was not run: an earlier action set to halt failed",
                    action.Type, workflow.Name);
                continue;
            }

            var halts = action.OnFailure == WorkflowFailurePolicy.Halt;

            var handler = actions.FirstOrDefault(a => a.Type == action.Type);
            if (handler == null)
            {
                logger.LogWarning("Unknown workflow action type '{ActionType}' in workflow '{WorkflowName}'. Skipping.", action.Type, workflow.Name);
                debugger.LogActionFailure(run, action.Type, Stopwatch.StartNew(),
                    $"No handler is registered for action type '{action.Type}'.", action.Parameters);
                halted = halts;
                continue;
            }

            var resolvedParams = new Dictionary<string, string>(action.Parameters.Count);
            var timer = debugger.StartAction(run, action.Type);

            try
            {
                // The same decryption the runner does, since a stored definition holds every
                // credential but Secret encrypted and an action expects to read them in clear.
                var (parameters, credentialError) = WebhookSigning.UnprotectCredentials(action.Parameters, protector);
                if (credentialError is not null)
                {
                    debugger.LogActionFailure(run, action.Type, timer, credentialError, action.Parameters);
                    halted = halts;
                    continue;
                }

                // Resolve {{...}} template variables against the content BEFORE executing, so live
                // runs behave like the dry-run preview.
                resolvedParams = await ActionParameters.ResolveAsync(
                    variableExtractor, action.Type, parameters, content, eventType, eventSequence: 0, ct);

                // The same channel the runner uses, so an action reads the trigger the same way on
                // either path.
                resolvedParams[ActionParameters.TriggerEventParameter] = eventType;

                logger.LogInformation("Executing workflow action '{ActionType}' for workflow '{WorkflowName}'", action.Type, workflow.Name);
                var result = await handler.RunAsync(resolvedParams, content, ct);

                if (result.Succeeded)
                {
                    debugger.LogActionSuccess(run, action.Type, timer, resolvedParams);

                    // The same note the runner records: a loop that stopped at its cap.
                    if (variableExtractor.Notes is { Count: > 0 } notes && run.Actions.Count > 0)
                    {
                        run.Actions[^1].ErrorMessage = string.Join(" ", notes);
                    }
                }
                else
                {
                    debugger.LogActionFailure(run, action.Type, timer, result.Error ?? "The action reported failure without a reason.", resolvedParams);
                    halted = halts;
                }
            }
            catch (Exception ex)
            {
                // Isolate per-action failures: a bad webhook/email must not prevent the remaining
                // actions in this workflow from running, unless the action is set to halt. An action
                // that throws is a failed action, which is what the run record has to say.
                debugger.LogActionFailure(run, action.Type, timer, ex, resolvedParams);
                logger.LogError(ex, "Workflow action '{ActionType}' in workflow '{WorkflowName}' failed", action.Type, workflow.Name);
                halted = halts;
            }
        }

        try
        {
            await debugger.CompleteExecutionAsync(run, overallTimer, ct);
        }
        catch (Exception ex)
        {
            // The actions already ran. Failing to write the record must not be reported as the
            // workflow failing, and must not reach the daemon.
            logger.LogError(ex, "Could not record the run of workflow '{WorkflowName}' ({WorkflowId})", workflow.Name, workflow.Id);
        }
    }
}
