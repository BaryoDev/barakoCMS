using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.GetWorkflowDebugInfo;

/// <summary>
/// Request to get workflow debug information.
/// </summary>
public class Request
{
    public Guid Id { get; set; }
    public int Limit { get; set; } = 20;
}

/// <summary>
/// Endpoint to get workflow execution history for debugging.
/// </summary>
public class Endpoint : Endpoint<Request, List<WorkflowExecutionLog>>
{
    private readonly IWorkflowDebugger _debugger;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(IWorkflowDebugger debugger, ILogger<Endpoint> logger)
    {
        _debugger = debugger;
        _logger = logger;
    }

    public override void Configure()
    {
        Get("/api/workflows/{id}/debug");
        AllowAnonymous(); // Allow for testing
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        try
        {
            var logs = await _debugger.GetExecutionHistoryAsync(req.Id, req.Limit, ct);
            await SendAsync(logs, cancellation: ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving debug info for workflow {WorkflowId}", req.Id);
            await SendErrorsAsync(cancellation: ct);
        }
    }
}
