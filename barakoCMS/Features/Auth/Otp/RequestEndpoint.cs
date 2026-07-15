using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using System.Security.Cryptography;

namespace barakoCMS.Features.Auth.Otp;

public class OtpRequest
{
    public string Email { get; set; } = string.Empty;
}

public class OtpRequestResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/auth/otp/request — email a 6-digit sign-in code to a registered user.
/// Always responds 200 with the same message so callers can't probe which emails exist.
/// </summary>
public class RequestEndpoint : Endpoint<OtpRequest, OtpRequestResponse>
{
    private readonly IDocumentSession _session;
    private readonly IEmailService _email;
    private readonly ILogger<RequestEndpoint> _logger;

    public RequestEndpoint(IDocumentSession session, IEmailService email, ILogger<RequestEndpoint> logger)
    {
        _session = session;
        _email = email;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/auth/otp/request");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth")); // 5 per 15 minutes per IP
    }

    public override async Task HandleAsync(OtpRequest req, CancellationToken ct)
    {
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();
        var ok = new OtpRequestResponse { Message = "If that email is registered, a sign-in code has been sent." };

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            await SendAsync(ok, cancellation: ct);
            return;
        }

        var user = await _session.Query<User>()
            .Where(u => u.Email.ToLower() == email)
            .FirstOrDefaultAsync(ct);
        if (user == null)
        {
            // Don't reveal non-existence; return the same response without sending.
            await SendAsync(ok, cancellation: ct);
            return;
        }

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

        var body =
            $"<p>Your BaryoClub sign-in code is:</p>" +
            $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>" +
            $"<p>It expires in 10 minutes. If you didn't request this, you can ignore this email.</p>";
        try
        {
            await _email.SendEmailAsync(user.Email, "Your BaryoClub sign-in code", body, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send OTP email to user {UserId}", user.Id);
            // Still return the neutral response; the code remains valid if they retry sending.
        }

        await SendAsync(ok, cancellation: ct);
    }
}
