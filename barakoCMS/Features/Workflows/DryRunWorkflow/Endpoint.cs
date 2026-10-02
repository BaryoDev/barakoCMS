using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace barakoCMS.Features.Workflows.DryRunWorkflow;

/// <summary>
/// Request to dry-run a workflow.
/// </summary>
internal class Request
{
    public DryRunWorkflowRequest Workflow { get; set; } = new();
    public barakoCMS.Models.Content SampleContent { get; set; } = new();
}

/// <summary>What create takes, and the id the simulation's log is filed under.</summary>
/// <remarks>
/// The id is a reference and not something stored on a workflow: the log of a dry run is listed with
/// the runs of the workflow it names, so the console sends the id of the one being edited. It is not
/// checked against the stored workflows, so a workflow that is not saved yet can be simulated.
/// </remarks>
internal sealed class DryRunWorkflowRequest : CreateWorkflowRequest
{
    public Guid Id { get; set; }
}

/// <summary>
/// Response for dry-run execution.
/// </summary>
internal class Response
{
    public bool Success { get; set; }
    public List<ActionExecutionLog> Actions { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Endpoint to test workflow execution without side effects (dry-run mode).
/// </summary>
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IWorkflowDebugger _debugger;
    private readonly ITemplateVariableExtractor _variableExtractor;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IWorkflowDebugger debugger,
        ITemplateVariableExtractor variableExtractor,
        ILogger<Endpoint> logger)
    {
        _debugger = debugger;
        _variableExtractor = variableExtractor;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/workflows/dry-run");
        // The authoring capability, not one of its own. This executes nothing, and withholding
        // the simulation from whoever wrote the workflow leaves production as the only way to see
        // what it does.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    /// <summary>The trigger the simulation resolves as: the first transition the workflow names, or its first event.</summary>
    private static string? SampleTrigger(CreateWorkflowRequest workflow)
    {
        var events = new[] { workflow.TriggerEvent }.Concat(workflow.TriggerEvents ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        return events.FirstOrDefault(WorkflowEvents.IsTransition) ?? events.FirstOrDefault();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var overallTimer = Stopwatch.StartNew();
        var executionLog = _debugger.StartExecution(req.Workflow.Id, req.SampleContent.Id, isDryRun: true);

        // The stored log is redacted, since anyone who can read runs can read it. The response goes
        // back to whoever sent the workflow and the sample, so it shows what the templates resolved to.
        var preview = new List<Dictionary<string, string>>();

        // One entry per preview entry. A Conditional branch that cannot be read is withheld from the
        // preview and named here. Not a failure: a real run refuses only the branch it takes, and
        // which one that is depends on the condition, which this simulation does not evaluate.
        var notes = new List<string?>();

        try
        {
            foreach (var action in req.Workflow.Actions)
            {
                var actionTimer = _debugger.StartAction(executionLog, action.Type);

                try
                {
                    // The sample entry is the caller's, so its author is not looked up: a simulation
                    // that read users would turn any id into that user's address.
                    await _variableExtractor.PrepareSampleAsync(
                        req.SampleContent, SampleTrigger(req.Workflow), action.Parameters.Values, ct);

                    var resolvedParams = ActionParameters.Resolve(_variableExtractor, action.Type, action.Parameters, req.SampleContent);

                    var shown = Actions.WebhookSigning.WithoutSecret(action.Type, resolvedParams, out var unreadableBranches);

                    // In dry-run mode, we just log what would happen without executing
                    _logger.LogInformation(
                        "DRY-RUN: Would execute {ActionType} with parameters: {Parameters}",
                        action.Type, System.Text.Json.JsonSerializer.Serialize(shown));

                    _debugger.LogActionSuccess(executionLog, action.Type, actionTimer, resolvedParams);
                    preview.Add(shown);
                    notes.Add(unreadableBranches.Count == 0
                        ? null
                        : string.Join(" ", unreadableBranches.Select(branch => Actions.WebhookSigning.UnreadableBranchWarning(branch))));
                }
                catch (Exception ex)
                {
                    _debugger.LogActionFailure(executionLog, action.Type, actionTimer, ex, action.Parameters);
                    preview.Add(Actions.WebhookSigning.WithoutSecret(action.Type, action.Parameters));
                    notes.Add(null);
                }
            }

            await _debugger.CompleteExecutionAsync(executionLog, overallTimer, ct);

            var response = new Response
            {
                Success = executionLog.Success,
                Actions = executionLog.Actions.Select((a, i) => new ActionExecutionLog
                {
                    ActionType = a.ActionType,
                    Success = a.Success,
                    ErrorMessage = a.ErrorMessage ?? (i < notes.Count ? notes[i] : null),
                    ResolvedParameters = i < preview.Count ? preview[i] : a.ResolvedParameters,
                    Duration = a.Duration,
                }).ToList(),
                Duration = executionLog.Duration,
                Message = executionLog.Success
                    ? "Dry-run completed successfully. No actual actions were executed."
                    : "Dry-run completed with errors."
            };

            await Send.ResponseAsync(response, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during workflow dry-run");
            await Send.ResponseAsync(new Response
            {
                Success = false,
                Message = $"Dry-run failed: {ex.Message}",
                Duration = overallTimer.Elapsed
            }, 500, ct);
        }
    }
}
