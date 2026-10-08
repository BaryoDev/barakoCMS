using System.Net;
using System.Net.Sockets;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Features.Settings.Email;
using barakoCMS.Infrastructure.Http;
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
    private readonly OutboundResilience _resilience;

    internal const string AttachmentsParameter = "Attachments";

    /// <summary>
    /// Creates a new EmailAction. Without a <paramref name="tenant"/> the email is sent as belonging
    /// to no tenant, and without <paramref name="files"/> an email that names an attachment fails.
    /// Without <paramref name="resilience"/> the default retry and breaker settings apply.
    /// </summary>
    public EmailAction(
        IEmailService emailService,
        ILogger<EmailAction> logger,
        TenantContext? tenant = null,
        IFileStore? files = null,
        IConfiguration? configuration = null,
        OutboundResilience? resilience = null)
    {
        _resilience = resilience ?? OutboundResilience.Default;
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
            await _resilience.RunAsync(
                EmailScope,
                _emailService.GetType().FullName ?? _emailService.GetType().Name,
                _resilience.Options.Retries,
                _resilience.Options.EmailAttemptTimeout,
                async (_, token) =>
                {
                    await SendAsync(to, subject, body, attachments, token);
                    return true;
                },
                WasNotSent,
                CountsAgainstProvider,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutboundCircuitOpenException)
        {
            // Retryable: the durable queue tries again after its backoff, by which time the breaker
            // has let a probe through. The provider is not named; the operator configured one.
            _logger.LogWarning("Email not sent: the provider's breaker is open.");
            return WorkflowActionResult.Failure(
                "The email provider is paused after repeated failures, so nothing was sent. The next attempt tries again.");
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

    private const string EmailScope = "email";

    // On the tenant's behalf: the run's scope carries the tenant whose workflow this is.
    private Task SendAsync(string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken ct) =>
        (attachments.Count, _tenant) switch
        {
            (0, null) => _emailService.SendEmailAsync(to, subject, body, ct),
            (0, { } tenant) => _emailService.SendForTenantAsync(tenant.Slug, to, subject, body, ct),
            (_, null) => _emailService.SendEmailAsync(to, subject, body, attachments, ct),
            (_, { } tenant) => _emailService.SendForTenantAsync(tenant.Slug, to, subject, body, attachments, ct),
        };

    /// <summary>
    /// A failure that happened before the provider could have accepted the message: the connection
    /// was never opened, or the provider answered that it did not take it. Only these are tried again
    /// inside the attempt. There is no idempotency key on a send, so a timeout or a connection lost
    /// mid-send may already have delivered the message, and resending it is a second email.
    /// </summary>
    internal static bool WasNotSent(Exception ex) => ex switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError } => true,
        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } => true,
        SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.TryAgain
            or SocketError.HostUnreachable or SocketError.NetworkUnreachable } => true,
        _ => false,
    };

    private static bool CountsAgainstProvider(Exception ex) =>
        WasNotSent(ex) || ex is TimeoutException or HttpRequestException or SocketException;

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
