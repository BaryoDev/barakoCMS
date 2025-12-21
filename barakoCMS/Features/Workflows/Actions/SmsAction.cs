using barakoCMS.Core.Interfaces;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending SMS messages.
/// </summary>
public class SmsAction : IWorkflowAction
{
    private readonly ISmsService _smsService;

    /// <summary>
    /// Creates a new SmsAction.
    /// </summary>
    public SmsAction(ISmsService smsService)
    {
        _smsService = smsService;
    }

    /// <inheritdoc />
    public string Type => "SMS";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var to = parameters.GetValueOrDefault("To", "+1234567890");
        var message = parameters.GetValueOrDefault("Message", $"Workflow triggered for content {content.Id}.");

        await _smsService.SendSmsAsync(to, message, ct);
    }
}

