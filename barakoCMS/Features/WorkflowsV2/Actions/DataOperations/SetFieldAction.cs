using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.DataOperations;

/// <summary>
/// Set a field value on the content.
/// </summary>
public class SetFieldAction : BaseWorkflowAction
{
    public SetFieldAction(ILogger<SetFieldAction> logger) : base(logger)
    {
    }

    public override string Type => "SetField";
    public override string Name => "Set Field";
    public override string Description => "Set the value of a field on the content.";
    public override string Category => ActionCategories.DataOperations;

    public override bool SupportsPreHook => true;
    public override bool SupportsPostHook => true;
    public override bool CanModifyData => true;

    public override Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            var field = GetRequiredString(action.Config, "field", context);
            var valueObj = action.Config.GetValueOrDefault("value");
            var value = ResolveTemplateVariables(valueObj?.ToString() ?? "", context);

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Would set field {Field} = {Value}", field, value);
                return Task.FromResult(Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["field"] = field,
                    ["value"] = value
                }));
            }

            // Handle special field paths
            if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            {
                var dataField = field.Substring(5);
                context.Content.Data[dataField] = value;
            }
            else if (field.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<barakoCMS.Models.ContentStatus>(value, true, out var status))
                {
                    context.Content.Status = status;
                }
                else
                {
                    return Task.FromResult(Failure($"Invalid status value: {value}"));
                }
            }
            else if (field.Equals("sensitivity", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<barakoCMS.Models.SensitivityLevel>(value, true, out var sensitivity))
                {
                    context.Content.Sensitivity = sensitivity;
                }
                else
                {
                    return Task.FromResult(Failure($"Invalid sensitivity value: {value}"));
                }
            }
            else
            {
                // Default to data field
                context.Content.Data[field] = value;
            }

            context.Content.UpdatedAt = DateTime.UtcNow;

            Logger.LogInformation("Set field {Field} = {Value} on content {ContentId}",
                field, value, context.Content.Id);

            return Task.FromResult(Success(new Dictionary<string, object>
            {
                ["field"] = field,
                ["value"] = value,
                ["previousValue"] = context.PreviousContent?.Data.GetValueOrDefault(field.Replace("data.", ""))
            }));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to set field");
            return Task.FromResult(Failure($"Failed to set field: {ex.Message}"));
        }
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("field"))
            errors.Add("'field' is required.");

        if (!config.ContainsKey("value"))
            errors.Add("'value' is required.");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "field", Type = "string", Description = "Field path to set (e.g., 'data.assignedTo', 'status')", Required = true },
                new() { Name = "value", Type = "any", Description = "Value to set. Supports template variables.", Required = true }
            },
            Required = new List<string> { "field", "value" },
            Example = @"{""field"": ""data.reviewedBy"", ""value"": ""{{user.id}}""}"
        };
    }
}
