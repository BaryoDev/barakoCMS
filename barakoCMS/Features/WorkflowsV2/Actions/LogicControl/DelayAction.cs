using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.LogicControl;

/// <summary>
/// Action that delays workflow execution.
/// </summary>
public class DelayAction : BaseWorkflowAction
{
    public override string Type => "Delay";
    public override string Name => "Delay Execution";
    public override string Category => ActionCategories.LogicControl;
    public override string Description => "Delay workflow execution for a specified duration";

    public DelayAction(ILogger<DelayAction> logger) : base(logger) { }

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var seconds = GetInt(action.Config, "seconds", 0);
        var minutes = GetInt(action.Config, "minutes", 0);
        var hours = GetInt(action.Config, "hours", 0);

        var totalDelay = TimeSpan.FromSeconds(seconds)
            + TimeSpan.FromMinutes(minutes)
            + TimeSpan.FromHours(hours);

        if (totalDelay <= TimeSpan.Zero)
        {
            return Success(new Dictionary<string, object>
            {
                ["delayed"] = false,
                ["message"] = "No delay configured"
            });
        }

        // Cap immediate delay at 5 minutes
        var maxImmediateDelay = TimeSpan.FromMinutes(5);

        if (totalDelay > maxImmediateDelay)
        {
            var resumeAt = DateTime.UtcNow + totalDelay;

            Logger.LogInformation("Scheduling workflow to resume at {ResumeAt}", resumeAt);

            return Success(new Dictionary<string, object>
            {
                ["delayed"] = true,
                ["scheduledFor"] = resumeAt.ToString("o"),
                ["message"] = $"Workflow scheduled to resume at {resumeAt:u}"
            });
        }

        if (context.IsDryRun)
        {
            Logger.LogInformation("[DRY-RUN] Would delay for {Delay}", totalDelay);
            return Success(new Dictionary<string, object>
            {
                ["dryRun"] = true,
                ["delayedFor"] = totalDelay.TotalSeconds
            });
        }

        Logger.LogInformation("Delaying workflow for {Delay}", totalDelay);

        await Task.Delay(totalDelay, context.CancellationToken);

        return Success(new Dictionary<string, object>
        {
            ["delayed"] = true,
            ["delayedFor"] = totalDelay.TotalSeconds,
            ["message"] = $"Delayed for {totalDelay.TotalSeconds} seconds"
        });
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
                new() { Name = "seconds", Type = "integer", Description = "Number of seconds to delay", Default = 0 },
                new() { Name = "minutes", Type = "integer", Description = "Number of minutes to delay", Default = 0 },
                new() { Name = "hours", Type = "integer", Description = "Number of hours to delay (max 24)", Default = 0 }
            },
            Example = @"{""minutes"": 5}"
        };
    }
}
