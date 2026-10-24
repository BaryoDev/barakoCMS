using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Security.Cryptography;

namespace barakoCMS.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IDocumentSession _session;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<OtpService> _logger;

    private readonly barakoCMS.Infrastructure.Auth.AuthMailQueue? _mail;

    public OtpService(IDocumentSession session, IEmailService email, IConfiguration config, ILogger<OtpService> logger)
        : this(session, email, config, logger, null)
    {
    }

    public OtpService(IDocumentSession session, IEmailService email, IConfiguration config, ILogger<OtpService> logger,
        barakoCMS.Infrastructure.Auth.AuthMailQueue? mail)
    {
        _mail = mail;
        _session = session;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task<bool> SendCodeAsync(string email, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct)
    {
        var issued = await IssueAsync(email, device, ct);
        if (issued is null)
        {
            return false;
        }

        try
        {
            await _email.SendEmailAsync(issued.Value.To, issued.Value.Subject, issued.Value.Body, ct);
            return true;
        }
        catch (Exception ex)
        {
            // The code is stored, so a retry can resend, but the caller has to know this attempt
            // did not reach anybody. It used to be swallowed here and reported as success, which
            // told the user to check an inbox nothing had been sent to.
            _logger.LogError(ex, "Failed to send OTP email");
            return false;
        }
    }

    /// <summary>
    /// Stores a code and hands the email to the background queue instead of sending it, for the
    /// anonymous request route, which must not take longer for an address that has an account.
    /// </summary>
    public async Task<bool> QueueCodeAsync(string email, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct)
    {
        if (_mail is null)
        {
            return await SendCodeAsync(email, device, ct);
        }

        var issued = await IssueAsync(email, device, ct);
        if (issued is null)
        {
            return false;
        }

        _mail.Enqueue(issued.Value.To, issued.Value.Subject, issued.Value.Body, "otp");
        return true;
    }

    private async Task<(string To, string Subject, string Body)?> IssueAsync(
        string email, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct)
    {
        email = (email ?? string.Empty).Trim().ToLowerInvariant();

        // Invalidate any outstanding codes for this email.
        var existing = await _session.Query<OtpCode>()
            .Where(o => o.Email == email && !o.Consumed)
            .ToListAsync(ct);
        foreach (var o in existing) { o.Consumed = true; _session.Update(o); }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _session.Store(new OtpCode
        {
            Email = email,
            CodeHash = BCrypt.Net.BCrypt.HashPassword(code),
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        });
        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // Invalidating the outstanding codes is an update of optimistic documents, so two
            // concurrent requests for the same address race. Nothing was stored, so no code exists
            // to send, and the caller has to hear that rather than be told to check an inbox.
            _logger.LogWarning("Concurrent OTP request for the same address; no code was issued");
            return null;
        }

        var appName = _config["Branding:AppName"] ?? "BarakoCMS";
        var html = WebUtility.HtmlEncode(appName);
        var body =
            $"<p>Your {html} sign-in code is:</p>" +
            $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>" +
            $"<p>It expires in 10 minutes.</p>" +
            $"<p>You are trying to sign in using <strong>{WebUtility.HtmlEncode(DeviceForEmail(device.UserAgent))}</strong> from {WebUtility.HtmlEncode(device.IpAddress)}. " +
            $"Sharing this code lets another device or person access your account. <strong>DO NOT SHARE.</strong> " +
            $"If this wasn't you, you can ignore this email.</p>";
        return (email, $"Your {appName} sign-in code", body);
    }

    private static readonly string[] Browsers = ["Edge", "Opera", "Chrome", "Firefox", "Safari"];
    private static readonly string[] Systems = ["iOS", "Android", "macOS", "Windows", "Linux"];

    /// <summary>
    /// Every description <see cref="barakoCMS.Infrastructure.DeviceContext.Describe"/> can produce by
    /// recognising a browser or an operating system, as opposed to its fallback, which is the
    /// user-agent itself.
    /// </summary>
    private static readonly HashSet<string> Recognised =
        [.. Browsers, .. Systems, .. Browsers.SelectMany(b => Systems.Select(s => $"{b} on {s}"))];

    /// <summary>
    /// The device as the email names it: a recognised browser and system, or a fixed phrase.
    /// </summary>
    /// <remarks>
    /// Never the raw user-agent. Anyone can send one, and in a sign-in email from this server,
    /// "call support at ..." reads as the server's own words whether or not it is escaped. A browser
    /// added to <c>Describe</c> but not to the lists here shows as unrecognised, which is the safe
    /// way round.
    /// </remarks>
    private static string DeviceForEmail(string userAgent)
    {
        var described = barakoCMS.Infrastructure.DeviceContext.Describe(userAgent);
        return Recognised.Contains(described) ? described : "an unrecognised device";
    }
}
