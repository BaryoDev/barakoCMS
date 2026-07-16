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

    public VerifyEndpoint(IDocumentSession session, IConfiguration config, barakoCMS.Core.Interfaces.IDeviceGate deviceGate)
    {
        _session = session;
        _config = config;
        _deviceGate = deviceGate;
    }

    private readonly barakoCMS.Core.Interfaces.IDeviceGate _deviceGate;

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

        var roles = await _session.Query<Role>()
            .Where(r => user.RoleIds.Contains(r.Id))
            .Select(r => r.Name)
            .ToListAsync(ct);

        // OTP proves possession of this device, so trust it. The gate (DeviceTrust module, if
        // installed) records/trusts the device and returns claims to bind the token to it.
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        var deviceClaims = await _deviceGate.TrustOnOtpAsync(user, device, ct);

        var jti = Guid.NewGuid().ToString();
        var accessTokenExpiry = DateTime.UtcNow.AddMinutes(15);
        var jwtToken = JWTBearer.CreateToken(
            signingKey: _config["JWT:Key"]!,
            expireAt: accessTokenExpiry,
            issuer: _config["JWT:Issuer"],
            audience: _config["JWT:Audience"],
            privileges: u =>
            {
                u.Claims.Add(new(JwtRegisteredClaimNames.Jti, jti));
                u.Claims.Add(new("UserId", user.Id.ToString()));
                u.Claims.Add(new("Username", user.Username));
                foreach (var role in roles)
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, role));
                if (!roles.Any())
                    u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, "User"));
                foreach (var claim in deviceClaims)
                    u.Claims.Add(claim);
            });

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
