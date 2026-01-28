using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.Communication;

/// <summary>
/// Action that sends an SMS message.
/// </summary>
public class SendSmsAction : BaseWorkflowAction
{
    private readonly ISmsService _smsService;

    public override string Type => "SendSms";
    public override string Name => "Send SMS";
    public override string Category => ActionCategories.Communication;
    public override string Description => "Send an SMS message";

    public SendSmsAction(ISmsService smsService, ILogger<SendSmsAction> logger) : base(logger)
    {
        _smsService = smsService;
    }

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        var to = GetRequiredString(action.Config, "to", context);
        var message = GetRequiredString(action.Config, "message", context);

        if (string.IsNullOrEmpty(to))
        {
            return Failure("SMS recipient (to) is required");
        }

        if (string.IsNullOrEmpty(message))
        {
            return Failure("SMS message is required");
        }

        if (context.IsDryRun)
        {
            Logger.LogInformation("[DRY-RUN] Would send SMS to {To}: {Message}", to, message);
            return Success(new Dictionary<string, object>
            {
                ["dryRun"] = true,
                ["to"] = to,
                ["message"] = message
            });
        }

        try
        {
            await _smsService.SendSmsAsync(to, message, context.CancellationToken);

            Logger.LogInformation("Sent SMS to {To}", to);

            return Success(new Dictionary<string, object>
            {
                ["to"] = to,
                ["messageSent"] = true
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send SMS to {To}", to);
            return Failure($"Failed to send SMS: {ex.Message}");
        }
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("to"))
            errors.Add("'to' is required");

        if (!config.ContainsKey("message"))
            errors.Add("'message' is required");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "to", Type = "string", Description = "Phone number to send SMS to", Required = true },
                new() { Name = "message", Type = "string", Description = "SMS message content", Required = true }
            },
            Required = new List<string> { "to", "message" },
            Example = @"{""to"": ""{{data.phone}}"", ""message"": ""Your order #{{data.orderId}} has been confirmed.""}"
        };
    }
}

/// <summary>
/// Interface for SMS sending service.
/// </summary>
public interface ISmsService
{
    Task SendSmsAsync(string to, string message, CancellationToken ct = default);
}

/// <summary>
/// No-op SMS service for when SMS is not configured.
/// </summary>
public class NoOpSmsService : ISmsService
{
    private readonly ILogger<NoOpSmsService> _logger;

    public NoOpSmsService(ILogger<NoOpSmsService> logger)
    {
        _logger = logger;
    }

    public Task SendSmsAsync(string to, string message, CancellationToken ct = default)
    {
        _logger.LogWarning("SMS service not configured. Would send to {To}: {Message}", to, message);
        return Task.CompletedTask;
    }
}
