using barakoCMS.Core.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BarakoCMS.Email.Smtp;

/// <summary>
/// Sends email through an SMTP relay using MailKit.
/// </summary>
/// <remarks>
/// MailKit rather than <c>System.Net.Mail.SmtpClient</c>, which Microsoft's own documentation says
/// not to use for new code.
///
/// A failure throws, the same as the Resend module. Every call site that treats mail as best effort
/// already catches (OtpService, EmailVerificationService, the workflow email action), and the two
/// that report the reason (the admin test send, the workflow action) need something to report.
/// Swallowing it here would make a dead relay look like a delivered message.
/// </remarks>
public sealed class SmtpEmailService : IEmailService
{
    private readonly IOptionsSnapshot<SmtpOptions> _options;
    private readonly IEmailSettingsProvider _settings;

    public SmtpEmailService(IOptionsSnapshot<SmtpOptions> options, IEmailSettingsProvider settings)
    {
        _options = options;
        _settings = settings;
    }

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
        SendEmailAsync(to, subject, body, Array.Empty<EmailAttachment>(), cancellationToken);

    public async Task SendEmailAsync(string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException(
                $"No SMTP host is set. Set {SmtpOptions.SectionName}:Host.");

        var from = await ResolveFromAsync(options, cancellationToken);

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = body };
        foreach (var attachment in attachments)
        {
            // MimeKit writes the name as an encoded parameter, so it cannot break out of its header.
            builder.Attachments.Add(attachment.FileName, attachment.Content, MimeTypeOf(attachment));
        }

        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var sending = false;

        try
        {
            await client.ConnectAsync(options.Host, options.Port, SecurityFor(options), cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.User))
                await client.AuthenticateAsync(options.User, options.Password ?? string.Empty, cancellationToken);

            sending = true;
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The relay's own words are the whole value of the message: "failed" alone sends an
            // operator back to guessing which of five settings is wrong. But a relay says whatever
            // it likes, and a server that echoes the credentials it just rejected would otherwise
            // put the password into an admin screen, a log and a support ticket. Redacted once,
            // here, over the finished sentence, rather than trusted not to appear.
            //
            // The relay's exception is deliberately not attached. Redacting only the outer message
            // left the raw one reachable through InnerException, and every call site logs the
            // exception object rather than its message: Serilog's default template ends in
            // {Exception}, which is ToString(), which concatenates the inner. The password went to
            // stdout on every failed send, and to disk wherever file logging is on. Keeping the
            // stack would mean keeping the leak, so the type name carries the diagnostic instead.
            var text = Redact($"SMTP send via {options.Host}:{options.Port} failed ({ex.GetType().Name}): {ex.Message}",
                options.Password);
            throw NothingSent(ex, sending) ? new EmailNotSentException(text) : new InvalidOperationException(text);
        }
    }

    /// <summary>
    /// Whether the relay cannot have the message, so sending it again cannot deliver it twice.
    /// </summary>
    /// <remarks>
    /// Anything <c>ConnectAsync</c> or <c>AuthenticateAsync</c> throws: a DNS failure or refused
    /// connection (<see cref="System.Net.Sockets.SocketException"/>), a failed TLS handshake
    /// (<see cref="SslHandshakeException"/>), a relay that does not offer STARTTLS
    /// (<see cref="NotSupportedException"/>), a refused login (<see cref="AuthenticationException"/>)
    /// or a broken greeting (<see cref="SmtpProtocolException"/>). No MAIL FROM has been sent yet.
    ///
    /// From <c>SendAsync</c>, only a 4xx or 5xx answer to MAIL FROM or RCPT TO, which MailKit raises as
    /// <see cref="SmtpCommandException"/> with <see cref="SmtpErrorCode.SenderNotAccepted"/> or
    /// <see cref="SmtpErrorCode.RecipientNotAccepted"/>. <see cref="SmtpErrorCode.MessageNotAccepted"/>
    /// is not on the list: MailKit raises it both for a refused DATA command and for a refusal after
    /// the whole message went over, and the two cannot be told apart. A dropped connection or a
    /// timeout during the send is not on it either, since the relay may have queued the message.
    /// </remarks>
    internal static bool NothingSent(Exception ex, bool sending) =>
        !sending
        || ex is SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted };

    private static ContentType MimeTypeOf(EmailAttachment attachment) =>
        ContentType.TryParse(attachment.ContentType, out var parsed)
            ? parsed
            : new ContentType("application", "octet-stream");

    /// <summary>
    /// The admin's from address if somebody set one, else the module's own.
    /// </summary>
    /// <remarks>
    /// The stored from address is the one part of the email settings surface that is not about
    /// Resend, so it carries across: an operator who types a sender at Settings, Email gets that
    /// sender, whichever provider is registered. The API key beside it does not carry across,
    /// because SMTP has no use for one.
    /// </remarks>
    private async Task<string> ResolveFromAsync(SmtpOptions options, CancellationToken ct)
    {
        var resolved = await _settings.GetAsync(ct);

        if (resolved.FromAddressSource == EmailSettingSource.Stored
            && !string.IsNullOrWhiteSpace(resolved.FromAddress))
            return resolved.FromAddress;

        if (!string.IsNullOrWhiteSpace(options.From))
            return options.From;

        throw new InvalidOperationException(
            "No sender address is set, in the admin under Settings, Email, or in "
            + $"{SmtpOptions.SectionName}:From.");
    }

    /// <summary>
    /// Unset means implicit TLS on 465 and STARTTLS everywhere else.
    /// </summary>
    /// <remarks>
    /// Deliberately not MailKit's <c>Auto</c>, which resolves to StartTlsWhenAvailable off 465 and
    /// therefore sends in the clear against a relay that does not advertise STARTTLS. Failing to
    /// connect is the better outcome, and plaintext stays available by asking for it by name.
    /// </remarks>
    internal static SecureSocketOptions SecurityFor(SmtpOptions options) =>
        (options.Security ?? (options.Port == 465 ? SmtpSecurity.SslOnConnect : SmtpSecurity.StartTls)) switch
        {
            SmtpSecurity.None => SecureSocketOptions.None,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

    private static string Redact(string message, string? secret) =>
        string.IsNullOrEmpty(secret) ? message : message.Replace(secret, "***", StringComparison.Ordinal);
}
