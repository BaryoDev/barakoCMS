using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;

namespace barakoCMS.Features.Auth.Otp;

public class OtpVerifyRequest
{
    public string Email { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class OtpVerifyResponse
{
    public string Token { get; set; } = string.Empty;
    public DateTime Expiry { get; set; }
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime RefreshTokenExpiry { get; set; }
}

/// <summary>
/// POST /api/auth/otp/verify — exchange a valid email code for the same JWT + refresh token that
/// password login issues. Single-use, expiry-checked, with a per-code attempt cap.
/// </summary>
public class VerifyEndpoint : Endpoint<OtpVerifyRequest, OtpVerifyResponse>
{
    private const int MaxAttempts = 5;

    private readonly IDocumentSession _session;
    private readonly IConfiguration _config;

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public VerifyEndpoint(IDocumentSession session, IConfiguration config, barakoCMS.Core.Interfaces.IDeviceGate deviceGate, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant, barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer)
    {
        _session = session;
        _config = config;
        _deviceGate = deviceGate;
        _tenant = tenant;
        _tokenIssuer = tokenIssuer;
    }

    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public override void Configure()
    {
        Post("/api/auth/otp/verify");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
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
            await _session.SaveChangesAsync(ct);
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
            await _session.SaveChangesAsync(ct);
            ThrowError("Invalid or expired code.");
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
            await _session.SaveChangesAsync(ct); // keep the code consumed
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
        await _session.SaveChangesAsync(ct);

        await SendAsync(new OtpVerifyResponse
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry,
        });
    }
}
