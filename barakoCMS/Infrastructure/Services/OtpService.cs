using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace barakoCMS.Infrastructure.Services;

public class OtpService : IOtpService
{
    private readonly IDocumentSession _session;
    private readonly IEmailService _email;
    private readonly IConfiguration _config;
    private readonly ILogger<OtpService> _logger;

    public OtpService(IDocumentSession session, IEmailService email, IConfiguration config, ILogger<OtpService> logger)
    {
        _session = session;
        _email = email;
        _config = config;
        _logger = logger;
    }

    public async Task SendCodeAsync(string email, barakoCMS.Infrastructure.DeviceContext device, CancellationToken ct)
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
        await _session.SaveChangesAsync(ct);

        var appName = _config["Branding:AppName"] ?? "BarakoCMS";
        var body =
            $"<p>Your {appName} sign-in code is:</p>" +
            $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>" +
            $"<p>It expires in 10 minutes.</p>" +
            $"<p>You are trying to sign in using <strong>{device.Description}</strong> from {device.IpAddress}. " +
            $"Sharing this code lets another device or person access your account. <strong>DO NOT SHARE.</strong> " +
            $"If this wasn't you, you can ignore this email.</p>";
        try
        {
            await _email.SendEmailAsync(email, $"Your {appName} sign-in code", body, ct);
        }
        catch (Exception ex)
        {
            // The code is stored; the caller still returns a neutral response so retrying can resend.
            _logger.LogError(ex, "Failed to send OTP email");
        }
    }
}
