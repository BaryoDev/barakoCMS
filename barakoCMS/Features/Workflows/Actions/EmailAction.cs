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
    OptionalParameters = new[] { "Attachments" },
    ExampleJson = @"{""Type"":""Email"",""Parameters"":{""To"":""admin@example.com"",""Subject"":""Workflow Triggered"",""Body"":""Content {{id}} was updated""}}"
)]
internal class EmailAction : IWorkflowAction
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailAction> _logger;
    private readonly TenantContext? _tenant;
    private readonly IFileStore? _files;
    private readonly EmailAttachmentLimits _limits;

    internal const string AttachmentsParameter = "Attachments";

    /// <summary>
    /// Creates a new EmailAction. Without a <paramref name="tenant"/> the email is sent as belonging
    /// to no tenant, and without <paramref name="files"/> an email that names an attachment fails.
    /// </summary>
    public EmailAction(
        IEmailService emailService,
        ILogger<EmailAction> logger,
        TenantContext? tenant = null,
        IFileStore? files = null,
        IConfiguration? configuration = null)
    {
        _emailService = emailService;
        _logger = logger;
        _tenant = tenant;
        _files = files;
        _limits = EmailAttachmentLimits.From(configuration);
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
        var to = parameters.GetValueOrDefault("To", "admin@example.com").Trim();
        var subject = parameters.GetValueOrDefault("Subject", $"Workflow Triggered for Content {content.Id}");
        var body = parameters.GetValueOrDefault("Body", $"Content '{content.ContentType}' with ID {content.Id} triggered this workflow.");

        if (!IsOneAddress(to))
        {
            // Permanent: the same entry resolves to the same recipient on every retry.
            return WorkflowActionResult.PermanentFailure(
                "The 'To' parameter must resolve to exactly one email address.");
        }

        // Everything that can refuse an attachment happens here, before the send. Nothing after the
        // send reads a file, so a message that went out is never failed over one.
        IReadOnlyList<EmailAttachment> attachments = Array.Empty<EmailAttachment>();
        var named = parameters.Where(p => p.Key.Equals(AttachmentsParameter, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count > 1)
        {
            return WorkflowActionResult.PermanentFailure($"The '{AttachmentsParameter}' parameter is declared more than once.");
        }

        if (named.Count == 1)
        {
            if (EmailProvider.IsMock(_emailService))
            {
                return WorkflowActionResult.PermanentFailure(
                    "No email provider is configured, so nothing was sent. Register a real IEmailService and restart.");
            }

            EmailAttachments.Resolution resolution;
            try
            {
                resolution = await EmailAttachments.ResolveAsync(named[0].Value, content, _files, _limits, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Reading an email attachment failed ({Exception}).", ex.GetType().Name);
                return WorkflowActionResult.Failure($"A file to attach could not be read ({ex.GetType().Name}).");
            }

            if (resolution.Refusal is not null)
            {
                return resolution.Refusal;
            }

            attachments = resolution.Files;
        }

        try
        {
            // On the tenant's behalf: the run's scope carries the tenant whose workflow this is.
            if (attachments.Count == 0)
            {
                if (_tenant is null)
                {
                    await _emailService.SendEmailAsync(to, subject, body, ct);
                }
                else
                {
                    await _emailService.SendForTenantAsync(_tenant.Slug, to, subject, body, ct);
                }
            }
            else if (_tenant is null)
            {
                await _emailService.SendEmailAsync(to, subject, body, attachments, ct);
            }
            else
            {
                await _emailService.SendForTenantAsync(_tenant.Slug, to, subject, body, attachments, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AttachmentsNotSupportedException)
        {
            // Permanent: the provider is the same one on every retry.
            return WorkflowActionResult.PermanentFailure("The registered email provider does not send attachments.");
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

    /// <summary>
    /// One address and nothing else. <c>To</c> is often filled from an entry field, and a field a
    /// form filled in must not turn one notification into a list: providers differ on whether they
    /// split commas and semicolons, so neither is accepted here, for any provider. The address is
    /// either the whole value or the part in angle brackets after a display name; MailAddress on its
    /// own reads "a@example.com b@example.com" as a display name and one address.
    /// </summary>
    private static bool IsOneAddress(string to) =>
        to.Length > 0
        && to.IndexOfAny([',', ';', '\r', '\n']) < 0
        && System.Net.Mail.MailAddress.TryCreate(to, out var parsed)
        && (to == parsed.Address || to.EndsWith($"<{parsed.Address}>", StringComparison.Ordinal));
}
