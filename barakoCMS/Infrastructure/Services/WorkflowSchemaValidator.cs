using barakoCMS.Models;

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
}

/// <summary>
/// Validates workflow definitions against schema and business rules.
/// </summary>
public class WorkflowSchemaValidator : IWorkflowSchemaValidator
{
    private readonly IWorkflowPluginRegistry _pluginRegistry;

    public WorkflowSchemaValidator(IWorkflowPluginRegistry pluginRegistry)
    {
        _pluginRegistry = pluginRegistry;
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

        if (string.IsNullOrWhiteSpace(workflow.TriggerContentType))
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
    }
}
