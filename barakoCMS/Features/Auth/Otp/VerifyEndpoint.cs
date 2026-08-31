using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;

namespace barakoCMS.Features.Auth.Otp;

internal class OtpVerifyRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

internal class OtpVerifyResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }

    /// <summary>
    /// True when the email code was correct but the account has MFA enabled: no tokens are issued. The
    /// client collects a TOTP/recovery code and calls /api/auth/mfa/verify with <see cref="MfaChallengeToken"/>.
    /// Mailbox possession is a first factor here, so it cannot stand in for the enrolled second factor.
    /// </summary>
    public bool RequiresMfa { get; set; }
    public string? MfaChallengeToken { get; set; }
}

/// <summary>
/// POST /api/auth/otp/verify — exchange a valid email code for the same JWT + refresh token that
/// password login issues. Single-use, expiry-checked, with a per-code attempt cap.
/// </summary>
internal class VerifyEndpoint : Endpoint<OtpVerifyRequest, OtpVerifyResponse>
{
    private const int MaxAttempts = 5;

    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public VerifyEndpoint(IDocumentSession session, IConfiguration config, barakoCMS.Core.Interfaces.IDeviceGate deviceGate, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant, barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer, barakoCMS.Infrastructure.Auth.Mfa.IMfaService mfa)
    {
        _session = session;
        _config = config;
        _deviceGate = deviceGate;
        _tenant = tenant;
        _tokenIssuer = tokenIssuer;
        _mfa = mfa;
    }

    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly barakoCMS.Infrastructure.Auth.Mfa.IMfaService _mfa;

    public override void Configure()
    {
        Post("/api/auth/otp/verify");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    /// <summary>
    /// Saves, reporting a lost optimistic-concurrency race rather than throwing.
    /// </summary>
    /// <remarks>
    /// <c>OtpCode</c> carries optimistic concurrency so two requests holding the same code cannot
    /// both consume it. That closes the race, but it means the loser's save throws, and an uncaught
    /// <c>ConcurrencyException</c> leaves this endpoint answering 500 to what is really just a code
    /// that has already been used. The loser is refused with the same message every other rejection
    /// here uses, so a caller cannot tell a lost race from a bad code.
    /// </remarks>
    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await _session.SaveChangesAsync(ct);
            return true;
        }
        catch (JasperFx.ConcurrencyException)
        {
            return false;
        }
    }

    public override async Task HandleAsync(OtpVerifyRequest req, CancellationToken ct)
    {
        var email = (req.Email ?? string.Empty).Trim().ToLowerInvariant();
        var code = (req.Code ?? string.Empty).Trim();

        var otp = (await _session.Query<OtpCode>()
                .Where(o => o.Email == email && !o.Consumed)
                .ToListAsync(ct))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefault();

        if (otp == null || otp.ExpiresAt < DateTime.UtcNow)
        {
            ThrowError("Invalid or expired code.");
            return;
        }
        if (otp.Attempts >= MaxAttempts)
        {
            ThrowError("Too many attempts. Please request a new code.");
            return;
        }

        if (!BCrypt.Net.BCrypt.Verify(code, otp.CodeHash))
        {
            otp.Attempts += 1;
            _session.Update(otp);
            // A lost race here means a concurrent request already touched this code. The answer is
            // the same either way, so the result of the save does not change it.
            await TrySaveAsync(ct);
            ThrowError("Invalid or expired code.");
            return;
        }

        // Consume the code so it can't be reused.
        otp.Consumed = true;
        _session.Update(otp);

        var user = await _session.Query<User>()
            .Where(u => u.Email.ToLower() == email)
            .FirstOrDefaultAsync(ct);
        if (user == null)
        {
            await TrySaveAsync(ct);
            ThrowError("Invalid or expired code.");
            return;
        }

        // Mailbox possession is only a first factor. If the account has MFA enrolled, a valid email code
        // must NOT mint tokens on its own — otherwise an inbox compromise defeats the second factor.
        // Return the same challenge the password path does; the client completes /api/auth/mfa/verify.
        if (await _mfa.IsEnabledAsync(user.Id, ct))
        {
            // Refuse on a lost race instead of issuing the challenge: the code was consumed by
            // the request that won, and one code must not yield two challenges.
            if (!await TrySaveAsync(ct)) { ThrowError("Invalid or expired code."); return; }
            var (challenge, _) = barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Create(_config, user.Id);
            await Send.ResponseAsync(new OtpVerifyResponse { RequiresMfa = true, MfaChallengeToken = challenge });
            return;
        }

        // OTP proves possession of this device, so trust it. The gate (DeviceTrust module, if
        // installed) records/trusts the device and returns claims to bind the token to it.
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        var deviceClaims = await _deviceGate.TrustOnOtpAsync(user, device, ct);

        // Proving control of the mailbox says who you are, not which tenants you belong to — the
        // issuer still decides whether a token for this tenant may be minted.
        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, _tenant.Slug, deviceClaims, ct);
        if (!issued.Allowed)
        {
            await TrySaveAsync(ct); // keep the code consumed; refused either way
            ThrowError("Invalid or expired code.");
            return;
        }

        var jti = issued.Jti;
        var accessTokenExpiry = issued.ExpiresAt;
        var jwtToken = issued.Token;

        var refreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        _session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshTokenString,
            UserId = user.Id,
            ExpiresAt = refreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });
        // The one that must not be best-effort. Losing here means another request consumed this
        // code, so returning the tokens computed above would mint a second session from one code,
        // which is the race the optimistic concurrency was added to stop.
        if (!await TrySaveAsync(ct)) { ThrowError("Invalid or expired code."); return; }

        // Also in a cookie page script cannot read. The body still carries it for
        // non-browser callers; see RefreshTokenCookie for why this is an addition.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Set(HttpContext, refreshTokenString, refreshTokenExpiry);

        await Send.ResponseAsync(new OtpVerifyResponse
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry,
        });
    }
}
