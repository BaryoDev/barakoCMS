using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Workflows.ValidateWorkflow;

/// <summary>
/// Request to validate a workflow definition.
/// </summary>
internal class Request
{
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty;
    public List<string> TriggerContentTypes { get; set; } = new();
    public string TriggerEvent { get; set; } = string.Empty;
    public List<string> TriggerEvents { get; set; } = new();
    public Dictionary<string, string> Conditions { get; set; } = new();
    public List<WorkflowAction> Actions { get; set; } = new();
    public string? Executor { get; set; }
    public string? Schedule { get; set; }
    public string? ScheduleTimeZone { get; set; }
    public string? ScheduleCatchUp { get; set; }
    public string? NotifyRoleOnFailure { get; set; }
}

/// <summary>
/// Endpoint to validate workflow JSON schema.
/// </summary>
internal class Endpoint(IWorkflowSchemaValidator validator) : Endpoint<Request, WorkflowValidationResult>
{
    public override void Configure()
    {
        Post("/api/workflows/validate");
        // Exposes internal workflow logic, so it sits with authoring rather than being read-only.
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var workflow = new WorkflowDefinition
        {
            Name = req.Name,
            TriggerContentType = req.TriggerContentType,
            TriggerContentTypes = req.TriggerContentTypes,
            TriggerEvent = req.TriggerEvent,
            TriggerEvents = req.TriggerEvents,
            Conditions = req.Conditions,
            Actions = req.Actions,
            Executor = req.Executor,
            Schedule = req.Schedule,
            ScheduleTimeZone = req.ScheduleTimeZone,
            ScheduleCatchUp = req.ScheduleCatchUp,
            NotifyRoleOnFailure = req.NotifyRoleOnFailure,
        };

        var result = await validator.ValidateAsync(workflow, ct);
        await Send.ResponseAsync(result, cancellation: ct);
    }
}

/// <summary>
/// A null action in the body is refused with a 400, as it was when a catch-all turned the
/// validator's exception into one. A Must rule, because FastEndpoints reads NotNull as making
/// `actions` required in the OpenAPI document, and a body without it is still accepted.
/// </summary>
internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.Actions).Must(a => a is null || a.TrueForAll(x => x is not null)).WithMessage("Action is required");
    }
}
