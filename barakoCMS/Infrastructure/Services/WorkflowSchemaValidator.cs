using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Interface for workflow schema validation.
/// </summary>
public interface IWorkflowSchemaValidator
{
    /// <summary>
    /// Validate a workflow definition.
    /// </summary>
    /// <param name="workflow">The workflow definition to validate.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>Validation result with any errors found.</returns>
    WorkflowValidationResult Validate(WorkflowDefinition workflow, CancellationToken ct = default);

    /// <summary>
    /// Validate a workflow definition, including the checks that need to read the triggering
    /// content type.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Validate"/> because a trigger naming a lifecycle transition can only
    /// be checked against the type that declares it, and that is a database read. The default
    /// implementation exists so an existing implementor still compiles; it skips the lifecycle check,
    /// so anything that saves a workflow calls this and not <see cref="Validate"/>.
    /// </remarks>
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken ct = default)
        => Task.FromResult(Validate(workflow, ct));
}

/// <summary>
/// Validates workflow definitions against schema and business rules.
/// </summary>
public class WorkflowSchemaValidator : IWorkflowSchemaValidator
{
    private readonly IWorkflowPluginRegistry _pluginRegistry;
    private readonly IQuerySession _session;
    private readonly bool _allowInsecureSignedUrls;

    public WorkflowSchemaValidator(IWorkflowPluginRegistry pluginRegistry, IQuerySession session)
        : this(pluginRegistry, session, configuration: null)
    {
    }

    public WorkflowSchemaValidator(IWorkflowPluginRegistry pluginRegistry, IQuerySession session, IConfiguration? configuration)
    {
        _pluginRegistry = pluginRegistry;
        _session = session;
        _allowInsecureSignedUrls = WebhookSigning.AllowsInsecureSignedUrls(configuration);
    }

    public WorkflowValidationResult Validate(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = new WorkflowValidationResult { IsValid = true };

        // Validate basic fields
        if (string.IsNullOrWhiteSpace(workflow.Name))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "name",
                Message = "Workflow name is required"
            });
            result.IsValid = false;
        }

        // TriggerContentType and TriggerContentTypes together name the types, so either one is
        // enough. A blank entry in the list is refused rather than skipped: it is a type the caller
        // meant to name and did not, and skipping it saves a workflow that never fires for it.
        var listed = workflow.TriggerContentTypes ?? [];
        for (var i = 0; i < listed.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(listed[i]))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = $"triggerContentTypes[{i}]",
                    Message = "A trigger content type cannot be blank"
                });
                result.IsValid = false;
            }
        }

        if (WorkflowTriggers.ContentTypes(workflow).Count == 0 && !listed.Any(string.IsNullOrWhiteSpace))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerContentType",
                Message = "Trigger content type is required"
            });
            result.IsValid = false;
        }

        if (string.IsNullOrWhiteSpace(workflow.TriggerEvent))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerEvent",
                Message = "Trigger event is required"
            });
            result.IsValid = false;
        }

        // Validate trigger event is a known value
        if (!string.IsNullOrWhiteSpace(workflow.TriggerEvent) && !WorkflowEvents.IsValid(workflow.TriggerEvent))
        {
            result.Errors.Add(new ValidationError
            {
                Field = "triggerEvent",
                Message = $"Trigger event must be one of: {string.Join(", ", WorkflowEvents.All)}"
            });
            result.IsValid = false;
        }

        // Validate actions
        if (workflow.Actions == null || workflow.Actions.Count == 0)
        {
            result.Errors.Add(new ValidationError
            {
                Field = "actions",
                Message = "At least one action is required"
            });
            result.IsValid = false;
        }
        else
        {
            for (int i = 0; i < workflow.Actions.Count; i++)
            {
                ValidateAction(workflow.Actions[i], i, result);
            }
        }

        return result;
    }

    /// <summary>
    /// Everything <see cref="Validate"/> checks, plus that a trigger naming a transition names one
    /// the triggering content type actually declares.
    /// </summary>
    /// <remarks>
    /// A workflow that names an undeclared transition saves happily and then never fires, and a
    /// workflow that never fires looks identical to one that fires and fails. Refusing it here is
    /// the only moment that is cheap to correct.
    ///
    /// This also settles the casing. The engine matches TriggerEvent with an equality query, while
    /// the lifecycle matches a transition name case insensitively, so "transition:approve" against a
    /// transition declared "Approve" would validate and then never match an event. The declared
    /// spelling is handed back in <see cref="WorkflowValidationResult.NormalisedTriggerEvent"/> for
    /// the caller to store.
    /// </remarks>
    public async Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition workflow, CancellationToken ct = default)
    {
        var result = Validate(workflow, ct);

        var transition = WorkflowEvents.TransitionName(workflow.TriggerEvent);
        if (transition is null or { Length: 0 })
        {
            return result;
        }

        // Every named type is checked, and every failure reported, so a list with two mistakes is
        // not fixed one save at a time.
        string? spelling = null;
        string? spelledBy = null;
        var transitionValid = true;

        foreach (var contentType in WorkflowTriggers.ContentTypes(workflow))
        {
            var definition = await _session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(d => d.Name == contentType, ct);

            // A missing type is refused rather than passed over. Skipping the check when the thing to
            // check against is absent is how a validation quietly stops validating, and here it would
            // let through exactly the workflow that never fires.
            if (definition is null)
            {
                result.Errors.Add(new ValidationError
                {
                    Field = "triggerEvent",
                    Message = $"Content type '{contentType}' does not exist, so its transitions cannot be checked",
                });
                transitionValid = false;
                continue;
            }

            var declared = definition.Lifecycle?.Transitions ?? new List<StateTransition>();
            var match = declared.FirstOrDefault(t => string.Equals(t.Name, transition, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                var available = declared.Count == 0
                    ? "(none)"
                    : string.Join(", ", declared.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

                result.Errors.Add(new ValidationError
                {
                    Field = "triggerEvent",
                    Message = $"'{transition}' is not a transition on '{contentType}'. Declared transitions: {available}",
                });
                transitionValid = false;
                continue;
            }

            // One TriggerEvent is stored and matched by equality, so the types have to agree on how
            // the transition is spelled, or the workflow would fire for some of them and not others.
            if (spelling is null)
            {
                spelling = match.Name;
                spelledBy = contentType;
            }
            else if (!string.Equals(spelling, match.Name, StringComparison.Ordinal))
            {
                result.Errors.Add(new ValidationError
                {
                    Field = "triggerEvent",
                    Message = $"'{spelledBy}' declares the transition as '{spelling}' and '{contentType}' as '{match.Name}'. "
                            + "One workflow matches one spelling, so use a workflow per spelling",
                });
                transitionValid = false;
            }
        }

        if (!transitionValid)
        {
            result.IsValid = false;
            return result;
        }

        if (spelling is not null)
        {
            result.NormalisedTriggerEvent = WorkflowEvents.ForTransition(spelling);
        }

        return result;
    }

    private void ValidateAction(WorkflowAction action, int index, WorkflowValidationResult result)
    {
        var fieldPrefix = $"actions[{index}]";

        // Check if action type is registered
        if (string.IsNullOrWhiteSpace(action.Type))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.type",
                Message = "Action type is required"
            });
            result.IsValid = false;
            return;
        }

        if (!_pluginRegistry.IsActionRegistered(action.Type))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.type",
                Message = $"Unknown action type '{action.Type}'. Available types: {string.Join(", ", _pluginRegistry.GetAllActions().Select(a => a.Type))}"
            });
            result.IsValid = false;
            return;
        }

        // Validate required parameters
        var metadata = _pluginRegistry.GetActionMetadata(action.Type);
        if (metadata != null && metadata.RequiredParameters.Any())
        {
            foreach (var requiredParam in metadata.RequiredParameters)
            {
                if (!action.Parameters.ContainsKey(requiredParam) ||
                    string.IsNullOrWhiteSpace(action.Parameters[requiredParam]))
                {
                    result.Errors.Add(new ValidationError
                    {
                        Field = $"{fieldPrefix}.parameters.{requiredParam}",
                        Message = $"Required parameter '{requiredParam}' is missing or empty"
                    });
                    result.IsValid = false;
                }
            }
        }

        if (string.Equals(action.Type, "Webhook", StringComparison.Ordinal)
            && WebhookSigning.IsInsecureSignedUrl(action.Parameters.GetValueOrDefault("Url"), action.Parameters, _allowInsecureSignedUrls))
        {
            result.Errors.Add(new ValidationError
            {
                Field = $"{fieldPrefix}.parameters.Url",
                Message = WebhookSigning.InsecureSignedUrlReason
            });
            result.IsValid = false;
        }
    }
}
