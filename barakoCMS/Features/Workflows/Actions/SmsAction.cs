using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending SMS messages.
/// </summary>
[WorkflowActionMetadata(
    Description = "Send SMS text messages",
    RequiredParameters = new[] { "To", "Message" },
    ExampleJson = @"{""Type"":""SMS"",""Parameters"":{""To"":""+1234567890"",""Message"":""Content {{id}} needs review""}}"
)]
internal class SmsAction : IWorkflowAction
{
    private readonly ISmsService _smsService;
    private readonly ILogger<SmsAction> _logger;

    /// <summary>
    /// Creates a new SmsAction.
    /// </summary>
    public SmsAction(ISmsService smsService, ILogger<SmsAction> logger)
    {
        _smsService = smsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "SMS";

    /// <summary>
    /// Only here because the interface still declares it. <see cref="RunAsync"/> is the contract
    /// this action implements, and delegating keeps a caller on the older path behaving the same.
    /// </summary>
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The mock provider sends nothing and never throws, so before this the action reported success
    /// for a message nobody received (#569). It is a configuration problem rather than a transient
    /// one, so it is a <see cref="WorkflowActionResult.PermanentFailure"/>: retrying does not help
    /// until a real <see cref="ISmsService"/> is registered.
    /// </remarks>
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var to = parameters.GetValueOrDefault("To", "+1234567890");
        var message = parameters.GetValueOrDefault("Message", $"Workflow triggered for content {content.Id}.");

        try
        {
            await _smsService.SendSmsAsync(to, message, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception type, not its message: a provider's own message routinely names the
            // number it was sending to, which is personal data and does not belong in a run record.
            _logger.LogWarning("SMS send failed ({Exception}).", ex.GetType().Name);
            return WorkflowActionResult.Failure($"The SMS provider failed ({ex.GetType().Name}).");
        }

        if (_smsService is MockSmsService)
        {
            return WorkflowActionResult.PermanentFailure(
                "No SMS provider is configured, so nothing was sent. Register a real ISmsService and restart.");
        }

        return WorkflowActionResult.Success();
    }
}

