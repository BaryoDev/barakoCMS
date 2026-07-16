using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure;
using barakoCMS.Models;

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
    private readonly IOtpService _otp;

    public RequestEndpoint(IDocumentSession session, IOtpService otp)
    {
        _session = session;
        _otp = otp;
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

        await _otp.SendCodeAsync(user.Email, DeviceContext.From(HttpContext), ct);
        await SendAsync(ok, cancellation: ct);
    }
}
