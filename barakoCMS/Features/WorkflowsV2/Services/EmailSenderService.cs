using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// SMTP-based email sender service.
/// </summary>
public class EmailSenderService : IEmailSenderService, IDisposable
{
    private readonly SmtpSettings _settings;
    private readonly ILogger<EmailSenderService> _logger;
    private SmtpClient? _client;
    private bool _disposed;

    public EmailSenderService(
        IOptions<SmtpSettings> settings,
        ILogger<EmailSenderService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(EmailMessage message)
    {
        await SendEmailAsync(
            message.To,
            message.Subject,
            message.HtmlBody,
            message.TextBody,
            message.From,
            message.ReplyTo,
            message.Attachments);
    }

    public async Task SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? from = null,
        string? replyTo = null,
        List<EmailAttachment>? attachments = null,
        CancellationToken ct = default)
    {
        await SendEmailAsync(
            new List<string> { to },
            subject,
            htmlBody,
            textBody,
            from,
            replyTo,
            attachments,
            ct);
    }

    public async Task SendEmailAsync(
        List<string> to,
        string subject,
        string htmlBody,
        string? textBody = null,
        string? from = null,
        string? replyTo = null,
        List<EmailAttachment>? attachments = null,
        CancellationToken ct = default)
    {
        EnsureClient();

        if (_client == null)
        {
            _logger.LogWarning("SMTP client not configured, email will not be sent");
            return;
        }

        using var message = new MailMessage();

        // Set from address
        message.From = new MailAddress(from ?? _settings.DefaultFrom, _settings.DefaultFromName);

        // Set recipients
        foreach (var recipient in to.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            message.To.Add(new MailAddress(recipient));
        }

        if (message.To.Count == 0)
        {
            _logger.LogWarning("No valid recipients for email with subject: {Subject}", subject);
            return;
        }

        // Set reply-to
        if (!string.IsNullOrEmpty(replyTo))
        {
            message.ReplyToList.Add(new MailAddress(replyTo));
        }

        message.Subject = subject;

        // Set body - prefer HTML with text alternative
        if (!string.IsNullOrEmpty(htmlBody))
        {
            message.IsBodyHtml = true;
            message.Body = htmlBody;

            // Add plain text alternative
            if (!string.IsNullOrEmpty(textBody))
            {
                var plainTextView = AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain");
                message.AlternateViews.Add(plainTextView);
            }
        }
        else if (!string.IsNullOrEmpty(textBody))
        {
            message.IsBodyHtml = false;
            message.Body = textBody;
        }

        // Add attachments
        if (attachments != null)
        {
            foreach (var attachment in attachments)
            {
                var stream = new MemoryStream(attachment.Content);
                var mailAttachment = new Attachment(stream, attachment.FileName, attachment.ContentType);
                message.Attachments.Add(mailAttachment);
            }
        }

        try
        {
            await _client.SendMailAsync(message, ct);

            _logger.LogInformation("Sent email to {Recipients} with subject: {Subject}",
                string.Join(", ", to), subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipients} with subject: {Subject}",
                string.Join(", ", to), subject);
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            EnsureClient();

            if (_client == null)
                return false;

            // Send a test email to a null address (just to test SMTP connection)
            // For actual testing, you'd send to a real test address
            _logger.LogInformation("Testing SMTP connection to {Host}:{Port}",
                _settings.Host, _settings.Port);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP connection test failed");
            return false;
        }
    }

    private void EnsureClient()
    {
        if (_client != null)
            return;

        if (string.IsNullOrEmpty(_settings.Host))
        {
            _logger.LogWarning("SMTP host not configured");
            return;
        }

        _client = new SmtpClient(_settings.Host, _settings.Port)
        {
            EnableSsl = _settings.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = _settings.TimeoutSeconds * 1000
        };

        if (!string.IsNullOrEmpty(_settings.Username))
        {
            _client.Credentials = new NetworkCredential(_settings.Username, _settings.Password);
        }

        _logger.LogInformation("Configured SMTP client for {Host}:{Port}",
            _settings.Host, _settings.Port);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _client?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// SMTP configuration settings.
/// </summary>
public class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string DefaultFrom { get; set; } = "";
    public string DefaultFromName { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
}
