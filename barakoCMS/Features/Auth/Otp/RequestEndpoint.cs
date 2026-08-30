using FastEndpoints;
using Marten;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure;
using barakoCMS.Models;

namespace barakoCMS.Features.Auth.Otp;

internal class OtpRequest
{
    public string Email { get; set; } = string.Empty;
}

internal class OtpRequestResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// POST /api/auth/otp/request — email a 6-digit sign-in code to a registered user.
/// Always responds 200 with the same message so callers can't probe which emails exist.
/// </summary>
internal class RequestEndpoint : Endpoint<OtpRequest, OtpRequestResponse>
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
            await Send.ResponseAsync(ok, cancellation: ct);
            return;
        }

        var user = await _session.Query<User>()
            .Where(u => u.Email.ToLower() == email)
            .FirstOrDefaultAsync(ct);
        if (user == null)
        {
            // Don't reveal non-existence; return the same response without sending.
            await Send.ResponseAsync(ok, cancellation: ct);
            return;
        }

        // The result is deliberately not reflected in the response here, and that is not the same
        // oversight this endpoint's sibling had. This route answers identically whether the account
        // exists, on purpose, so reporting a send failure would tell an unauthenticated caller that
        // the address is real. Enumeration protection outranks the better error message on a route
        // anybody can call. The failure is logged at Error inside the service.
        //
        // The device approval path in Features/Auth/Login is different: the caller has already
        // proved the password, so there is nothing left to enumerate and it does report the failure.
        _ = await _otp.SendCodeAsync(user.Email, DeviceContext.From(HttpContext), ct);
        await Send.ResponseAsync(ok, cancellation: ct);
    }
}
