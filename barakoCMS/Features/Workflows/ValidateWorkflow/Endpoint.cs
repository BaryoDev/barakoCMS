using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.ValidateWorkflow;

/// <summary>
/// Request to validate a workflow definition.
/// </summary>
internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty;
    public string TriggerEvent { get; set; } = string.Empty;
    public Dictionary<string, string> Conditions { get; set; } = new();
    public List<WorkflowAction> Actions { get; set; } = new();
}

/// <summary>
/// Endpoint to validate workflow JSON schema.
/// </summary>
internal class Endpoint : Endpoint<Request, WorkflowValidationResult>
{
    private readonly IWorkflowSchemaValidator _validator;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(IWorkflowSchemaValidator validator, ILogger<Endpoint> logger)
    {
        _validator = validator;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/workflows/validate");
        // Exposes internal workflow logic, so it sits with authoring rather than being read-only.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        try
        {
            var workflow = new WorkflowDefinition
            {
                Name = req.Name,
                TriggerContentType = req.TriggerContentType,
                TriggerEvent = req.TriggerEvent,
                Conditions = req.Conditions,
                Actions = req.Actions
            };

            var result = await _validator.ValidateAsync(workflow, ct);
            await Send.ResponseAsync(result, cancellation: ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Workflow validation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating workflow");
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
