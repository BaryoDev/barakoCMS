using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.GetWorkflowDebugInfo;

/// <summary>
/// Request to get workflow debug information.
/// </summary>
internal class Request
{
    public Guid Id { get; set; }
    public int Limit { get; set; } = 20;
}

/// <summary>
/// Endpoint to get workflow execution history for debugging.
/// </summary>
internal class Endpoint(
    IWorkflowDebugger debugger,
    ILogger<Endpoint> logger) : Endpoint<Request, List<WorkflowExecutionLog>>
{
    public override void Configure()
    {
        Get("/api/workflows/{id}/debug");
        // The execution log of what already ran, which is the run list from the other end, so it
        // reads with the runs rather than with authoring.
        Definition.RequireCapability(SystemCapabilities.ViewWorkflowRuns, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        try
        {
            var logs = await debugger.GetExecutionHistoryAsync(req.Id, req.Limit, ct);
            await Send.ResponseAsync(logs, cancellation: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving debug info for workflow {WorkflowId}", req.Id);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
