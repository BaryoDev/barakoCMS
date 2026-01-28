using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.LogicControl;

/// <summary>
/// Action that logs a message for debugging and audit purposes.
/// </summary>
public class LogAction : BaseWorkflowAction
{
    public override string Type => "Log";
    public override string Name => "Log Message";
    public override string Category => ActionCategories.LogicControl;
    public override string Description => "Log a message for debugging or audit";

    public LogAction(ILogger<LogAction> logger) : base(logger) { }

    public override Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var message = GetString(action.Config, "message", context, "Workflow log entry");
        var level = GetString(action.Config, "level", context, "information");
        var includeContext = GetBool(action.Config, "includeContext", false);

        var logData = new Dictionary<string, object>
        {
            ["workflowId"] = context.Workflow.Id.ToString(),
            ["contentType"] = context.Content.ContentType,
            ["contentId"] = context.Content.Id.ToString()
        };

        if (includeContext)
        {
            logData["data"] = context.Content.Data;
        }

        switch (level.ToLowerInvariant())
        {
            case "debug":
                Logger.LogDebug("Workflow Log: {Message} {@Data}", message, logData);
                break;
            case "warning":
                Logger.LogWarning("Workflow Log: {Message} {@Data}", message, logData);
                break;
            case "error":
                Logger.LogError("Workflow Log: {Message} {@Data}", message, logData);
                break;
            default:
                Logger.LogInformation("Workflow Log: {Message} {@Data}", message, logData);
                break;
        }

        return Task.FromResult(Success(new Dictionary<string, object>
        {
            ["message"] = message,
            ["level"] = level,
            ["logged"] = true
        }));
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        return new List<string>(); // No required fields
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "message", Type = "string", Description = "Message to log" },
                new() { Name = "level", Type = "string", Description = "Log level", Enum = new List<string> { "debug", "information", "warning", "error" } },
                new() { Name = "includeContext", Type = "boolean", Description = "Include workflow context data in log", Default = false }
            },
            Example = @"{""message"": ""Processing {{data.name}}"", ""level"": ""information""}"
        };
    }
}
