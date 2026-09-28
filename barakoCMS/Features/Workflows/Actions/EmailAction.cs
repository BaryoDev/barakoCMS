using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Features.Settings.Email;
using barakoCMS.Infrastructure.Multitenancy;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending emails.
/// </summary>
[WorkflowActionMetadata(
    Description = "Send email notifications",
    Group = WorkflowActionGroup.Comms,
    RequiredParameters = new[] { "To", "Subject", "Body" },
    ExampleJson = @"{""Type"":""Email"",""Parameters"":{""To"":""admin@example.com"",""Subject"":""Workflow Triggered"",""Body"":""Content {{id}} was updated""}}"
)]
internal class EmailAction : IWorkflowAction
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailAction> _logger;
    private readonly TenantContext? _tenant;

    /// <summary>
    /// Creates a new EmailAction. Without a <paramref name="tenant"/> the email is sent as belonging
    /// to no tenant.
    /// </summary>
    public EmailAction(IEmailService emailService, ILogger<EmailAction> logger, TenantContext? tenant = null)
    {
        _emailService = emailService;
        _logger = logger;
        _tenant = tenant;
    }

    /// <inheritdoc />
    public string Type => "Email";

    /// <summary>
    /// Only here because the interface still declares it. <see cref="RunAsync"/> is the contract
    /// this action implements, and delegating keeps a caller on the older path behaving the same.
    /// </summary>
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    /// <inheritdoc />
    /// <remarks>
    /// The mock provider sends nothing and never throws, so before this the action reported success
    /// for a message nobody received (#569, same shape as SmsAction). It is a configuration problem
    /// rather than a transient one, so it is a <see cref="WorkflowActionResult.PermanentFailure"/>:
    /// retrying does not help until a real <see cref="IEmailService"/> is registered.
    /// </remarks>
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var to = parameters.GetValueOrDefault("To", "admin@example.com");
        var subject = parameters.GetValueOrDefault("Subject", $"Workflow Triggered for Content {content.Id}");
        var body = parameters.GetValueOrDefault("Body", $"Content '{content.ContentType}' with ID {content.Id} triggered this workflow.");

        try
        {
            // On the tenant's behalf: the run's scope carries the tenant whose workflow this is.
            if (_tenant is null)
            {
                await _emailService.SendEmailAsync(to, subject, body, ct);
            }
            else
            {
                await _emailService.SendForTenantAsync(_tenant.Slug, to, subject, body, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception type, not its message: a provider's own message routinely names the
            // recipient it was sending to, which is personal data and does not belong in a run record.
            _logger.LogWarning("Email send failed ({Exception}).", ex.GetType().Name);
            return WorkflowActionResult.Failure($"The email provider failed ({ex.GetType().Name}).");
        }

        if (EmailProvider.IsMock(_emailService))
        {
            return WorkflowActionResult.PermanentFailure(
                "No email provider is configured, so nothing was sent. Register a real IEmailService and restart.");
        }

        return WorkflowActionResult.Success();
    }
}

