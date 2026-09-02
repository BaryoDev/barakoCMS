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

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
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
        message.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

        using var client = new SmtpClient();

        try
        {
            await client.ConnectAsync(options.Host, options.Port, SecurityFor(options), cancellationToken);

            if (!string.IsNullOrWhiteSpace(options.User))
                await client.AuthenticateAsync(options.User, options.Password ?? string.Empty, cancellationToken);

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
            throw new InvalidOperationException(
                Redact($"SMTP send via {options.Host}:{options.Port} failed: {ex.Message}", options.Password),
                ex);
        }
    }

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
