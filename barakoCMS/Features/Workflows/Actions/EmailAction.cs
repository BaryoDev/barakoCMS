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
    OptionalParameters = new[] { "Attachments", "Template" },
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
    private readonly Marten.IDocumentSession? _session;
    private readonly barakoCMS.Infrastructure.Services.ITemplateVariableExtractor? _extractor;

    internal const string AttachmentsParameter = "Attachments";

    /// <summary>
    /// Creates a new EmailAction. Without a <paramref name="tenant"/> the email is sent as belonging
    /// to no tenant, and without <paramref name="files"/> an email that names an attachment fails.
    /// <paramref name="resilience"/> carries the optional send timeout and the retries; without it a
    /// send has no timeout and the default retries. <paramref name="session"/> is where a named
    /// template is read, in the run's tenant; without it an email that names a template fails.
    /// <paramref name="extractor"/> resolves the template's placeholders the way the runner resolved
    /// the parameters; without it they resolve as an unprepared template does.
    /// </summary>
    public EmailAction(
        IEmailService emailService,
        ILogger<EmailAction> logger,
        TenantContext? tenant = null,
        IFileStore? files = null,
        IConfiguration? configuration = null,
        OutboundResilience? resilience = null,
        Marten.IDocumentSession? session = null,
        barakoCMS.Infrastructure.Services.ITemplateVariableExtractor? extractor = null)
    {
        _session = session;
        _extractor = extractor;
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

        if (parameters.TryGetValue(barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.TemplateParameter, out var templateName)
            && !string.IsNullOrWhiteSpace(templateName))
        {
            string? refusal;
            try
            {
                (subject, body, refusal) = await FromTemplateAsync(templateName, content, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Retryable: a read that failed may not fail again.
                _logger.LogWarning("Reading an email template failed ({Exception}).", ex.GetType().Name);
                return WorkflowActionResult.Failure($"The email template could not be read ({ex.GetType().Name}).");
            }

            if (refusal is not null)
            {
                return WorkflowActionResult.PermanentFailure(refusal);
            }
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

        // Retried inside the attempt only on EmailNotSentException, which a provider throws only when
        // the message cannot have left. Any other failure may be a message the relay took, and there
        // is no idempotency key to make a second send safe, so it is sent once. No breaker: nothing
        // counts against it. How many tries fit is EmailRetries: with a send timeout, that timeout
        // covers every try and the waits between them; without one, the provider's own bound on a
        // not-sent try decides, and a provider with no bound is sent once.
        var sendTimeout = _resilience.Options.EmailSendTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (sendTimeout is { } limit) deadline.CancelAfter(limit);

        var lastNotSent = false;
        try
        {
            await _resilience.RunAsync(
                "email",
                _tenant?.Slug ?? "",
                _emailService.GetType().Name,
                _resilience.Options.EmailRetries(_emailService.MaxNotSentDuration),
                System.Threading.Timeout.InfiniteTimeSpan,
                async (_, token) =>
                {
                    lastNotSent = false;
                    try
                    {
                        await SendAsync(to, subject, body, attachments, token);
                    }
                    catch (EmailNotSentException)
                    {
                        lastNotSent = true;
                        throw;
                    }

                    return true;
                },
                ex => ex is EmailNotSentException,
                _ => false,
                deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && lastNotSent)
        {
            // The limit ran out between tries, and the last one did not leave.
            _logger.LogWarning("Email send ran out of time between tries, and nothing was sent.");
            return WorkflowActionResult.Failure("The email provider did not take the email before the send timeout.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Thrown on, not returned as a failure: the runner records a timeout as unknown and does
            // not retry it, because the message may already have gone and a retry is a second email.
            var seconds = sendTimeout?.TotalSeconds ?? 0;
            _logger.LogWarning("Email send did not finish within {Seconds} s.", seconds);
            throw new OperationCanceledException(
                $"The email provider did not finish within {seconds:0.#} s, so it is not known whether the email was sent.");
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
    /// The subject and body of the published template a workflow names, resolved against the entry
    /// with the same encodings an inline subject and body get, or why it cannot be sent.
    /// </summary>
    /// <remarks>
    /// Read through the run's session, so only a template of the run's tenant is found. The
    /// extractor is asked to read what the template names first, the way the runner did for the
    /// parameters, so a reference or a site format in a template resolves as it would inline.
    /// </remarks>
    private async Task<(string Subject, string Body, string? Refusal)> FromTemplateAsync(
        string name, barakoCMS.Models.Content content, CancellationToken ct)
    {
        if (_session is null)
        {
            return (string.Empty, string.Empty, "This Email action was built without the content store, so it cannot read a template.");
        }

        var (rendered, error) = await barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.ForSendingAsync(_session, name, ct);
        if (rendered is null)
        {
            return (string.Empty, string.Empty, error);
        }

        var texts = new Dictionary<string, string> { ["Subject"] = rendered.Subject, ["Body"] = rendered.Html };
        Dictionary<string, string> resolved;
        if (_extractor is null)
        {
            resolved = ActionParameters.Resolve(Type, texts, content);
        }
        else
        {
            await _extractor.PrepareMoreAsync(content, texts.Values, ct);
            resolved = ActionParameters.Resolve(_extractor, Type, texts, content);
        }

        return (resolved["Subject"], barakoCMS.Features.EmailTemplates.EmailTemplateRenderer.Finish(resolved["Body"]), null);
    }

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
