using barakoCMS.Features.Workflows;
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
public class Request
{
    public WorkflowDefinition Workflow { get; set; } = new();
    public barakoCMS.Models.Content SampleContent { get; set; } = new();
}

/// <summary>
/// Response for dry-run execution.
/// </summary>
public class Response
{
    public bool Success { get; set; }
    public List<ActionExecutionLog> Actions { get; set; } = new();
    public TimeSpan Duration { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Endpoint to test workflow execution without side effects (dry-run mode).
/// </summary>
public class Endpoint : Endpoint<Request, Response>
{
    private readonly IDocumentSession _session;
    private readonly IEnumerable<IWorkflowAction> _actions;
    private readonly IWorkflowDebugger _debugger;
    private readonly ITemplateVariableExtractor _variableExtractor;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        IDocumentSession session,
        IEnumerable<IWorkflowAction> actions,
        IWorkflowDebugger debugger,
        ITemplateVariableExtractor variableExtractor,
        ILogger<Endpoint> logger)
    {
        _session = session;
        _actions = actions;
        _debugger = debugger;
        _variableExtractor = variableExtractor;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/workflows/dry-run");
        AllowAnonymous(); // Allow for testing
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var overallTimer = Stopwatch.StartNew();
        var executionLog = _debugger.StartExecution(req.Workflow.Id, req.SampleContent.Id, isDryRun: true);

        try
        {
            foreach (var action in req.Workflow.Actions)
            {
                var actionTimer = _debugger.StartAction(executionLog, action.Type);

                try
                {
                    // Resolve template variables in parameters
                    var resolvedParams = new Dictionary<string, string>();
                    foreach (var param in action.Parameters)
                    {
                        resolvedParams[param.Key] = _variableExtractor.ResolveVariables(param.Value, req.SampleContent);
                    }

                    // In dry-run mode, we just log what would happen without executing
                    _logger.LogInformation(
                        "DRY-RUN: Would execute {ActionType} with parameters: {Parameters}",
                        action.Type, System.Text.Json.JsonSerializer.Serialize(resolvedParams));

                    _debugger.LogActionSuccess(executionLog, action.Type, actionTimer, resolvedParams);
                }
                catch (Exception ex)
                {
                    _debugger.LogActionFailure(executionLog, action.Type, actionTimer, ex, action.Parameters);
                }
            }

            await _debugger.CompleteExecutionAsync(executionLog, overallTimer, ct);

            var response = new Response
            {
                Success = executionLog.Success,
                Actions = executionLog.Actions,
                Duration = executionLog.Duration,
                Message = executionLog.Success
                    ? "Dry-run completed successfully. No actual actions were executed."
                    : "Dry-run completed with errors."
            };

            await SendAsync(response, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during workflow dry-run");
            await SendAsync(new Response
            {
                Success = false,
                Message = $"Dry-run failed: {ex.Message}",
                Duration = overallTimer.Elapsed
            }, 500, ct);
        }
    }
}
