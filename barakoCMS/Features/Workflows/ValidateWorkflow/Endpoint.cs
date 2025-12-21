using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.ValidateWorkflow;

/// <summary>
/// Request to validate a workflow definition.
/// </summary>
public class Request
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
public class Endpoint : Endpoint<Request, WorkflowValidationResult>
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
        AllowAnonymous(); // Allow for testing
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

            var result = _validator.Validate(workflow);
            await SendAsync(result, cancellation: ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Workflow validation was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating workflow");
            await SendErrorsAsync(cancellation: ct);
        }
    }
}
