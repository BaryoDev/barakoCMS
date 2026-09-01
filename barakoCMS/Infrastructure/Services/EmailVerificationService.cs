using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Services;

public class EmailVerificationService : IEmailVerificationService
{
    /// <summary>Where the token is handed back. The API route is the fallback when no SPA is configured.</summary>
    private const string VerifyPath = "/auth/verify-email";

    private readonly IDocumentSession _session;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<EmailVerificationService> _logger;

    public EmailVerificationService(
        IDocumentSession session,
        IEmailService email,
        IConfiguration config,
        ILogger<EmailVerificationService> logger)
    {
        _session = session;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task<bool> IssueAsync(string username, string email, string passwordHash, CancellationToken ct)
    {
        email = (email ?? string.Empty).Trim().ToLowerInvariant();

        // Any earlier attempt on this address is spent. Otherwise registering twice leaves two live
        // tokens, and the first one still opens an account with whatever username and password that
        // attempt named, which is not what the person who registered second asked for.
        var outstanding = await _session.Query<PendingRegistration>()
            .Where(p => p.Email == email && !p.Consumed)
            .ToListAsync(ct);
        foreach (var stale in outstanding)
        {
            stale.Consumed = true;
            _session.Update(stale);
        }

        var pending = new PendingRegistration
        {
            Username = username,
            Email = email,
            PasswordHash = passwordHash,
            ExpiresAt = DateTime.UtcNow.Add(EmailVerificationOptions.TokenLifetime),
        };

        var (token, hash) = EmailVerificationToken.Create(pending.Id);
        pending.TokenHash = hash;
        _session.Store(pending);

        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            // Two registrations for one address arriving together race on invalidating each other's
            // outstanding rows. Nothing was stored, so there is no token to send. Same shape as
            // OtpService, and the caller answers identically either way.
            _logger.LogWarning("Concurrent registration for the same address; no verification token was issued");
            return false;
        }

        var appName = AppName;
        var body =
            $"<p>Somebody asked to create a {appName} account with this email address.</p>"
          + $"<p>If that was you, confirm the address to finish:</p>"
          + Confirmation(token)
          + $"<p>The link expires in {(int)EmailVerificationOptions.TokenLifetime.TotalHours} hours and works once. "
          + "Until it is used, no account exists. If this wasn't you, ignore this email and nothing happens.</p>";

        return await SendAsync(email, $"Confirm your {appName} email address", body, ct);
    }

    public Task<bool> SendAlreadyRegisteredAsync(string email, CancellationToken ct)
    {
        var appName = AppName;
        var body =
            $"<p>Somebody asked to create a {appName} account with this email address, but it is already registered.</p>"
          + "<p>If that was you, sign in instead, or use the sign-in code option if you have forgotten your password.</p>"
          + "<p>No new account was created and nothing about your account has changed. "
          + "If this wasn't you, you can ignore this email.</p>";

        return SendAsync(email, $"Your {appName} account already exists", body, ct);
    }

    private string AppName => _config["Branding:AppName"] ?? "BarakoCMS";

    /// <summary>
    /// A clickable link where the deployment has told us its public URL, and the bare token where it
    /// has not. The host header is never used to build it: a link in an email is read by somebody
    /// else later, which is exactly the case <see cref="CanonicalHost"/> refuses to guess at.
    /// </summary>
    private string Confirmation(string token)
    {
        var baseUrl = _config[CanonicalHost.BaseUrlKey]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            return $"<p style=\"font-family:monospace;word-break:break-all\">{token}</p>";
        }

        var link = $"{baseUrl}{VerifyPath}?token={Uri.EscapeDataString(token)}";
        return $"<p><a href=\"{link}\">Confirm this email address</a></p>"
             + $"<p style=\"font-family:monospace;word-break:break-all\">{link}</p>";
    }

    private async Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        try
        {
            await _email.SendEmailAsync(to, subject, body, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Logged, never returned to the caller. Which of the two mails failed says whether the
            // address was already registered, and the register endpoint exists not to answer that.
            _logger.LogError(ex, "Failed to send a registration email");
            return false;
        }
    }
}
