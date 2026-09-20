using FastEndpoints;
using Marten;
using Marten.Patching;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Models;
using System.Security.Cryptography;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/verify — complete a two-step login. Takes the challenge token issued by
/// /api/auth/login (RequiresMfa) plus a TOTP or recovery code, and on success returns the same JWT +
/// refresh token password login issues. Wrong codes count toward the same lockout as password failures,
/// so the 6-digit space can't be brute-forced.
/// </summary>
internal class VerifyEndpoint(
    IMfaService mfa,
    IDocumentSession session,
    IQuerySession query,
    IConfiguration config,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    barakoCMS.Infrastructure.Auth.AccountLockout lockout) : Endpoint<VerifyRequest, VerifyResponse>
{
    public override void Configure()
    {
        Post("/api/auth/mfa/verify");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(VerifyRequest req, CancellationToken ct)
    {
        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);

        var userId = MfaChallengeToken.ValidatedUserId(config, req.ChallengeToken);
        if (userId is null)
        {
            ThrowError("Your sign-in session expired. Enter your password again.");
            return;
        }

        var user = await query.LoadAsync<User>(userId.Value, ct);
        if (user is null || !await mfa.IsEnabledAsync(user.Id, ct))
        {
            // The challenge was minted for this user, so this is an odd state; stay generic.
            ThrowError("Invalid code.");
            return;
        }

        // Same lockout gate as password login — a locked account can't be MFA-brute-forced either.
        if (user.LockoutUntil is { } until && until > DateTime.UtcNow)
        {
            var remaining = (int)(until - DateTime.UtcNow).TotalMinutes + 1;
            ThrowError($"Account is locked due to multiple failed attempts. Please try again in {remaining} minute(s).");
            return;
        }

        if (!await mfa.VerifyCodeAsync(user.Id, req.Code, ct))
        {
            session.Patch<User>(user.Id).Increment(x => x.FailedLoginAttempts);
            await session.SaveChangesAsync(ct);

            var attempts = (await query.LoadAsync<User>(user.Id, ct))?.FailedLoginAttempts ?? 0;
            if (attempts >= barakoCMS.Infrastructure.Auth.AccountLockout.MaxFailedAttempts
                && await lockout.TryLockAsync(user, ct))
            {
                await AuditLog.RecordAsync(session, tenant.Slug, "auth.account.locked", user.Id, user.Username,
                    metadata: new() { ["attempts"] = attempts, ["factor"] = "mfa" }, ipAddress: device.IpAddress, ct: ct);
            }
            await AuditLog.RecordAsync(session, tenant.Slug, "auth.mfa.failed", user.Id, user.Username,
                metadata: new() { ["attempts"] = attempts }, ipAddress: device.IpAddress, ct: ct);
            await session.SaveChangesAsync(ct);

            ThrowError("Invalid code.");
            return;
        }

        // Success: clear the failed-attempt counter, mint tokens through the issuer (which still runs the
        // tenant-access check), and drop a refresh token — the same shape as password login.
        if (user.FailedLoginAttempts > 0 || user.LockoutUntil.HasValue)
        {
            session.Patch<User>(user.Id).Set(x => x.FailedLoginAttempts, 0);
            session.Patch<User>(user.Id).Set(x => x.LockoutUntil, (DateTime?)null);
        }

        // Completing MFA proves possession of this device; trust it and bind the token to it, so
        // MFA-issued tokens carry the same device claim (did) as the password and OTP paths.
        var deviceClaims = await deviceGate.TrustOnOtpAsync(user, device, ct);
        var issued = await tokenIssuer.IssueAccessTokenAsync(user, tenant.Slug, deviceClaims, ct);
        if (!issued.Allowed)
        {
            await session.SaveChangesAsync(ct);
            ThrowError("Invalid code.");
            return;
        }

        var refreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshTokenString,
            UserId = user.Id,
            ExpiresAt = refreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });
        await AuditLog.RecordAsync(session, tenant.Slug, "auth.mfa.succeeded", user.Id, user.Username,
            ipAddress: device.IpAddress, ct: ct);
        await session.SaveChangesAsync(ct);

        // Also in a cookie page script cannot read. The body still carries it for
        // non-browser callers; see RefreshTokenCookie for why this is an addition.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Set(HttpContext, refreshTokenString, refreshTokenExpiry);

        await Send.ResponseAsync(new VerifyResponse
        {
            Token = issued.Token,
            Expiry = issued.ExpiresAt,
            RefreshToken = refreshTokenString,
            RefreshTokenExpiry = refreshTokenExpiry,
        });
    }
}
