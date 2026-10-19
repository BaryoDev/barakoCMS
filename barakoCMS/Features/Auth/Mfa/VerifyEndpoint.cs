using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Auth.Mfa;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using System.Security.Claims;
using System.Security.Cryptography;

namespace barakoCMS.Features.Auth.Mfa;

/// <summary>
/// POST /api/auth/mfa/verify — complete a two-step login. Takes the challenge token issued by
/// /api/auth/login (RequiresMfa) plus a TOTP or recovery code, and on success returns the same JWT +
/// refresh token password login issues. Wrong codes count toward the same lockout as password failures,
/// so the 6-digit space can't be brute-forced.
/// </summary>
[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class VerifyEndpoint(
    IMfaService mfa,
    IDocumentSession session,
    IQuerySession query,
    IConfiguration config,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
    barakoCMS.Core.Interfaces.IDeviceGate deviceGate,
    AccountLockout lockout,
    IPermissionResolver permissions,
    IContentBatchRunner transaction) : Endpoint<VerifyRequest, VerifyResponse>
{
    private sealed record SignedIn(TokenIssueResult Issued, string Refresh, DateTime RefreshExpiry);

    public override void Configure()
    {
        Post("/api/auth/mfa/verify");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting("auth"));
    }

    public override async Task HandleAsync(VerifyRequest req, CancellationToken ct)
    {
        var device = DeviceContext.From(HttpContext);

        var challenge = MfaChallengeToken.Validated(config, req.ChallengeToken);
        if (challenge is null)
        {
            ThrowError("Your sign-in session expired. Enter your password again.");
            return;
        }

        var user = await query.LoadAsync<User>(challenge.Value.UserId, ct);
        if (user is null || !await mfa.IsEnabledAsync(user.Id, ct))
        {
            // The challenge was minted for this user, so this is an odd state; stay generic.
            ThrowError("Invalid code.");
            return;
        }

        // Same lockout gate as password login — a locked account can't be MFA-brute-forced either.
        if (AccountLockout.LockedMessage(user) is { } locked)
        {
            ThrowError(locked);
            return;
        }

        if (!await mfa.VerifyCodeAsync(user.Id, req.Code, ct))
        {
            await lockout.RecordWrongCodeAsync(session, query, tenant.Slug, user, "mfa", "auth.mfa.failed",
                device.IpAddress, ct);

            ThrowError("Invalid code.");
            return;
        }

        // Success: clear the failed-attempts, then mint as password login does.
        AccountLockout.ClearFailures(session, user);
        await session.SaveChangesAsync(ct);

        // Completing MFA proves possession of this device; trust it and bind the token to it, so
        // MFA-issued tokens carry the same device claim (did) as the password and OTP paths.
        var deviceClaims = await deviceGate.TrustOnOtpAsync(user, device, ct);
        var signedIn = await SignInAsync(user, challenge.Value.Grant, deviceClaims, device, ct);
        if (signedIn is null)
        {
            ThrowError("Invalid code.");
            return;
        }

        // Also in a cookie page script cannot read. The body still carries it for
        // non-browser callers; see RefreshTokenCookie for why this is an addition.
        RefreshTokenCookie.Set(HttpContext, signedIn.Refresh, signedIn.RefreshExpiry);

        await Send.ResponseAsync(new VerifyResponse
        {
            Token = signedIn.Issued.Token,
            Expiry = signedIn.Issued.ExpiresAt,
            RefreshToken = signedIn.Refresh,
            RefreshTokenExpiry = signedIn.RefreshExpiry,
        });
    }

    /// <summary>
    /// Grants what the challenge carries, issues the token and stores the refresh token in one
    /// database transaction, or null when the issuer refused. The issuer reads memberships, and it
    /// reads the grant through that transaction, so a refused sign-in grants nothing.
    /// </summary>
    private async Task<SignedIn?> SignInAsync(
        User user, PendingExternalGrant? grant, IReadOnlyList<Claim> deviceClaims, DeviceContext device,
        CancellationToken ct)
    {
        var granted = false;
        SignedIn? signedIn;
        try
        {
            signedIn = await transaction.RunAsync<SignedIn?>(async (scope, token) =>
            {
                var writes = scope.GetRequiredService<IDocumentSession>();
                granted = await StageGrantAsync(writes, user, grant, token);
                await writes.SaveChangesAsync(token);

                var issued = await scope.GetRequiredService<ITokenIssuer>()
                    .IssueAccessTokenAsync(user, tenant.Slug, deviceClaims, token);
                if (!issued.Allowed)
                {
                    return new ContentBatchOutcome<SignedIn?>(null, Commit: false);
                }

                var result = await StageSignInAsync(writes, user, issued, device, token);
                return new ContentBatchOutcome<SignedIn?>(result, Commit: true);
            }, ct);
        }
        catch (Exception ex) when (barakoCMS.Infrastructure.Filters.IdempotencyFilter.IsUniqueViolation(ex))
        {
            // Another verify of this challenge spent its grant first.
            return null;
        }

        if (signedIn is not null && granted)
        {
            permissions.InvalidateUserPermissions(user.Id);
        }

        return signedIn;
    }

    /// <summary>
    /// Stages the roles an external sign-in earned at first factor, now that the second has passed,
    /// and records the grant as spent, so the same challenge with another valid code grants nothing.
    /// The roles are checked again and the membership must still be Active, as at a sign-in with no
    /// second factor.
    /// </summary>
    /// <returns>True when a membership changed.</returns>
    private async Task<bool> StageGrantAsync(
        IDocumentSession writes, User user, PendingExternalGrant? grant, CancellationToken ct)
    {
        if (grant is null || !string.Equals(grant.TenantSlug, tenant.Slug, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spent = SpentGrantJti(grant);
        if (await writes.Query<RevokedToken>().AnyAsync(r => r.TokenJti == spent, ct))
        {
            return false;
        }

        // Insert, not Store: a second verify racing this one fails on the unique jti at commit.
        writes.Insert(new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenJti = spent,
            UserId = user.Id,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(MfaChallengeToken.Lifetime).AddMinutes(1),
            Reason = "mfa_grant_spent",
        });

        return await ExternalRoleGrant.StageAsync(writes, user, grant.TenantSlug, grant.RoleIds, grant.Source, ct);
    }

    /// <summary>
    /// The spent grant's key in the revoked token store, which already expires and purges what it
    /// holds. Prefixed so it can never equal an access token's jti, which is a bare guid.
    /// </summary>
    private static string SpentGrantJti(PendingExternalGrant grant) => $"mfa-grant:{grant.Id:N}";

    private async Task<SignedIn> StageSignInAsync(
        IDocumentSession writes, User user, TokenIssueResult issued, DeviceContext device, CancellationToken ct)
    {
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var refreshExpiry = DateTime.UtcNow.Add(issued.RefreshLifetime);
        writes.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = RefreshToken.HashOf(refresh),
            UserId = user.Id,
            ExpiresAt = refreshExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            DeviceId = device.DeviceId,
        });
        await AuditLog.RecordAsync(writes, tenant.Slug, "auth.mfa.succeeded", user.Id, user.Username,
            ipAddress: device.IpAddress, ct: ct);
        return new SignedIn(issued, refresh, refreshExpiry);
    }
}
