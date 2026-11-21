using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Refresh;

[barakoCMS.Infrastructure.Filters.NoIdempotentReplay]
internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _querySession;
    private readonly IDocumentSession _documentSession;
    private readonly ILogger<Endpoint> _logger;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;
    private readonly IConfiguration _configuration;

    public Endpoint(
        IQuerySession querySession,
        IDocumentSession documentSession,
        ILogger<Endpoint> logger,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer,
        IConfiguration configuration)
    {
        _configuration = configuration;
        _querySession = querySession;
        _documentSession = documentSession;
        _logger = logger;
        _tenant = tenant;
        _tokenIssuer = tokenIssuer;
    }

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public override void Configure()
    {
        Post("/api/auth/refresh");
        AllowAnonymous(); // No auth required, validated by refresh token
        barakoCMS.Infrastructure.Auth.Mfa.MfaEnrollment.AllowDuringMfaEnrollment(Definition);
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.RefreshPolicy));
    }

    private Task RevokeSessionsAsync(Guid userId, string reason, CancellationToken ct) =>
        barakoCMS.Infrastructure.Auth.RevokeRefreshTokens.ForUserAsync(
            _documentSession, userId, reason, ct, Resolve<barakoCMS.Infrastructure.Services.ISessionEpochService>());

    private async Task RevokeAfterRaceAsync(Guid userId, Guid sessionId, CancellationToken ct)
    {
        // The rotation never committed, so drop it before looking: the session still holds it.
        _documentSession.EjectAllPendingChanges();

        // Losing to a session end is not reuse: answer 401 and leave the user's other sessions alone.
        if (await barakoCMS.Infrastructure.Auth.SessionRevocation.IsEndedAsync(_documentSession, sessionId, ct))
        {
            ThrowError("Refresh token has been revoked. Please log in again.", 401);
            return;
        }

        _logger.LogWarning(
            "Concurrent refresh-token use detected for UserId: {UserId}. Rejecting duplicate rotation.",
            userId);

        await RevokeSessionsAsync(userId, "reuse_detected", ct);
        await _documentSession.SaveChangesAsync(ct);
        ThrowError("Refresh token was already used. Please log in again.", 401);
    }

    /// <summary>
    /// The unused successor of a used token that is being retried, or null when this is reuse. A
    /// retry is one replay, inside the window, from the same device, while the successor has never
    /// been used or revoked. A row with no session id never qualifies.
    /// </summary>
    private async Task<RefreshToken?> RetrySuccessorAsync(RefreshToken used, CancellationToken ct)
    {
        var window = barakoCMS.Infrastructure.Auth.RefreshRetryWindow.Read(_configuration);
        if (window <= TimeSpan.Zero)
            return null;

        if (used.SessionId is not { } sessionId || used.SuccessorId is not { } successorId || used.RevokedAt is not { } rotatedAt)
            return null;

        if (DateTime.UtcNow - rotatedAt > window)
            return null;

        if (!string.IsNullOrEmpty(used.DeviceId)
            && used.DeviceId != barakoCMS.Infrastructure.DeviceContext.From(HttpContext).DeviceId)
            return null;

        // Through the document session, so the successor's version is guarded with the rest.
        var successor = await _documentSession.LoadAsync<RefreshToken>(successorId, ct);
        if (successor is null || successor.IsRevoked || successor.SessionId != sessionId || successor.UserId != used.UserId)
            return null;

        return successor;
    }

    private static List<System.Security.Claims.Claim> RefreshClaims(RefreshToken refreshToken, Guid sessionId)
    {
        var claims = new List<System.Security.Claims.Claim>();
        // Preserve device binding so DeviceTrust enforcement still applies to refreshed tokens.
        if (!string.IsNullOrEmpty(refreshToken.DeviceId))
            claims.Add(new("did", refreshToken.DeviceId));
        claims.Add(barakoCMS.Infrastructure.Auth.SessionRevocation.ClaimFor(sessionId));
        return claims;
    }

    private async Task<User> LoadUsableUserAsync(RefreshToken refreshToken, CancellationToken ct)
    {
        var user = await _querySession.LoadAsync<User>(refreshToken.UserId, ct);
        if (user == null)
        {
            _logger.LogError("User not found for valid refresh token. UserId: {UserId}", refreshToken.UserId);
            ThrowError("User not found", 401);
        }

        if (barakoCMS.Infrastructure.Auth.AccountLockout.LockedMessage(user) is not null
            || barakoCMS.Infrastructure.Auth.AccountBlock.Is(user))
        {
            _logger.LogWarning("Refresh refused for locked or blocked account. UserId: {UserId}", user.Id);
            ThrowError("Invalid refresh token", 401);
        }

        return user;
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Body first, then the cookie. The body keeps working for every non-browser caller, and the
        // admin sends neither: its cookie rides along automatically and page script never holds the
        // value at all.
        var fromBody = !string.IsNullOrWhiteSpace(req.RefreshToken);
        var presented = fromBody
            ? req.RefreshToken
            : barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Read(HttpContext);

        if (string.IsNullOrWhiteSpace(presented))
        {
            // Same answer as an unknown token. "You sent nothing" and "that is not a token" are the
            // same thing to a caller, and saying which is which tells an attacker their probe was
            // well formed.
            _logger.LogWarning("Refresh attempt with no token");
            ThrowError("Invalid refresh token", 401);
            return;
        }

        // Load via the document session so Marten tracks the version for the optimistic-concurrency
        // guard on rotation below.
        var refreshToken = await barakoCMS.Infrastructure.Auth.RefreshTokenLookup.FindAsync(_documentSession, presented, ct);

        if (refreshToken == null)
        {
            _logger.LogWarning("Refresh attempt with invalid token");
            ThrowError("Invalid refresh token", 401);
            return;
        }

        // Set when a used token is a retry: its successor was never used, and is replaced below.
        RefreshToken? unusedSuccessor = null;
        if (refreshToken.IsRevoked && refreshToken.RevokedReason == "used")
            unusedSuccessor = await RetrySuccessorAsync(refreshToken, ct);

        if (refreshToken.IsRevoked && unusedSuccessor is null)
        {
            // Reuse detection: replaying an already-rotated ("used") token is a strong signal that
            // the token family is compromised — revoke every active token for the user.
            if (refreshToken.RevokedReason == "used")
            {
                _logger.LogWarning(
                    "Refresh token reuse detected. Revoking all tokens for UserId: {UserId}",
                    refreshToken.UserId);
                await RevokeSessionsAsync(refreshToken.UserId, "reuse_detected", ct);
                await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.token.reuse_detected", refreshToken.UserId, null,
                    ipAddress: barakoCMS.Infrastructure.DeviceContext.From(HttpContext).IpAddress, ct: ct);
                await _documentSession.SaveChangesAsync(ct);
            }
            else
            {
                _logger.LogWarning(
                    "Refresh attempt with revoked token. UserId: {UserId}, Reason: {Reason}",
                    refreshToken.UserId, refreshToken.RevokedReason);
            }
            ThrowError("Refresh token has been revoked. Please log in again.", 401);
            return;
        }

        if (refreshToken.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Refresh attempt with expired token. UserId: {UserId}, Expired: {ExpiresAt}",
                refreshToken.UserId, refreshToken.ExpiresAt);
            ThrowError("Refresh token has expired. Please log in again.", 401);
            return;
        }

        var user = await LoadUsableUserAsync(refreshToken, ct);

        // The client sends X-Tenant on refresh, so without a membership check a refresh is a free
        // tenant hop: present a legitimately-issued refresh token with a different X-Tenant and walk
        // out with a token scoped to a tenant you never belonged to. The issuer re-checks every time,
        // which also means a membership revoked mid-session stops working at the next refresh rather
        // than lingering until the refresh token expires.
        // A row from before sessions had no id. Its next refresh gives the new row one.
        var sessionId = refreshToken.SessionId ?? Guid.NewGuid();
        var extraClaims = RefreshClaims(refreshToken, sessionId);

        var issued = await _tokenIssuer.IssueAccessTokenAsync(user, _tenant.Slug, extraClaims, ct);
        if (!issued.Allowed)
        {
            _logger.LogWarning(
                "Refresh refused for {UserId} on tenant {Tenant}: {Reason}",
                user.Id, _tenant.Slug, issued.DenialReason);
            ThrowError("Refresh token is not valid for this tenant. Please log in again.", 401);
            return;
        }

        var jti = issued.Jti;
        var accessTokenExpiry = issued.ExpiresAt;
        var jwtToken = issued.Token;

        // Generate new refresh token (rotation)
        var newRefreshTokenString = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var newRefreshTokenExpiry = DateTime.UtcNow.Add(issued.RefreshLifetime);
        
        // Device binding, session id and sign-in facts carry across rotation. Without the device id,
        // device trust stopped enforcing anything from the second refresh onward.
        // A retry builds on the successor the client never saw, so the session facts it holds carry on.
        var newRefreshToken = (unusedSuccessor ?? refreshToken).Successor(
            newRefreshTokenString, newRefreshTokenExpiry,
            barakoCMS.Infrastructure.DeviceContext.From(HttpContext), sessionId);

        if (unusedSuccessor is null)
        {
            // Revoke old refresh token (rotation)
            refreshToken.IsRevoked = true;
            refreshToken.RevokedReason = "used";
            refreshToken.RevokedAt = DateTime.UtcNow;
            // The used row keeps no address or device text. The new row holds them.
            refreshToken.ClearIdentifiers();
            refreshToken.SuccessorId = newRefreshToken.Id;
        }
        else
        {
            // The retry is spent: the old token has no successor to point at, so a second replay is
            // reuse. The unused successor and the new one commit with it, under the same guard.
            refreshToken.SuccessorId = null;
            unusedSuccessor.IsRevoked = true;
            unusedSuccessor.RevokedReason = "superseded";
            unusedSuccessor.RevokedAt = DateTime.UtcNow;
            unusedSuccessor.ClearIdentifiers();
            _documentSession.Update(unusedSuccessor);
        }

        _documentSession.Update(refreshToken);
        _documentSession.Store(newRefreshToken);
        await AuditLog.RecordAsync(_documentSession, _tenant.Slug,
            unusedSuccessor is null ? "auth.token.refreshed" : "auth.token.refresh_retried",
            user.Id, user.Username,
            ipAddress: barakoCMS.Infrastructure.DeviceContext.From(HttpContext).IpAddress, ct: ct);

        try
        {
            // Optimistic-concurrency guard: if another request rotated this same token first,
            // this commit throws and we reject rather than issuing a second valid token.
            await _documentSession.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            await RevokeAfterRaceAsync(refreshToken.UserId, sessionId, ct);
            return;
        }

        _logger.LogInformation(
            "Token refreshed for user: {Username}, UserId: {UserId}",
            user.Username, user.Id);

        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Set(HttpContext, newRefreshTokenString, newRefreshTokenExpiry);

        // A caller that sent the token in the body reads the replacement from the body. One that
        // sent only the cookie is a browser, and this route is anonymous, so any script on the
        // page's origin can make the same call: the body must not hand it what the cookie hides.
        await Send.ResponseAsync(new Response
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = fromBody ? newRefreshTokenString : string.Empty,
            RefreshTokenExpiry = newRefreshTokenExpiry
        });
    }
}
