using FastEndpoints;
using FastEndpoints.Security;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Refresh;

internal class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _querySession;
    private readonly IDocumentSession _documentSession;
    private readonly IConfiguration _config;
    private readonly ILogger<Endpoint> _logger;
    private readonly barakoCMS.Infrastructure.Services.ITokenRevocationService _tokenRevocation;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        IQuerySession querySession,
        IDocumentSession documentSession,
        IConfiguration config,
        ILogger<Endpoint> logger,
        barakoCMS.Infrastructure.Services.ITokenRevocationService tokenRevocation,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant,
        barakoCMS.Infrastructure.Auth.ITokenIssuer tokenIssuer)
    {
        _querySession = querySession;
        _documentSession = documentSession;
        _config = config;
        _logger = logger;
        _tokenRevocation = tokenRevocation;
        _tenant = tenant;
        _tokenIssuer = tokenIssuer;
    }

    private readonly barakoCMS.Infrastructure.Auth.ITokenIssuer _tokenIssuer;

    public override void Configure()
    {
        Post("/api/auth/refresh");
        AllowAnonymous(); // No auth required, validated by refresh token
        Options(x => x.RequireRateLimiting("auth")); // Rate limit to prevent brute-force attacks
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // Body first, then the cookie. The body keeps working for every non-browser caller, and the
        // admin sends neither: its cookie rides along automatically and page script never holds the
        // value at all.
        var presented = !string.IsNullOrWhiteSpace(req.RefreshToken)
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
        var refreshToken = await _documentSession.Query<RefreshToken>()
            .FirstOrDefaultAsync(rt => rt.Token == presented, ct);

        if (refreshToken == null)
        {
            _logger.LogWarning("Refresh attempt with invalid token");
            ThrowError("Invalid refresh token", 401);
            return;
        }

        // Check if token is revoked
        if (refreshToken.IsRevoked)
        {
            // Reuse detection: replaying an already-rotated ("used") token is a strong signal that
            // the token family is compromised — revoke every active token for the user.
            if (refreshToken.RevokedReason == "used")
            {
                _logger.LogWarning(
                    "Refresh token reuse detected. Revoking all tokens for UserId: {UserId}",
                    refreshToken.UserId);
                await _tokenRevocation.RevokeAllUserTokensAsync(refreshToken.UserId, "reuse_detected", ct);
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

        // Check if token is expired
        if (refreshToken.ExpiresAt < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Refresh attempt with expired token. UserId: {UserId}, Expired: {ExpiresAt}",
                refreshToken.UserId, refreshToken.ExpiresAt);
            ThrowError("Refresh token has expired. Please log in again.", 401);
            return;
        }

        // Load the user
        var user = await _querySession.LoadAsync<User>(refreshToken.UserId, ct);
        if (user == null)
        {
            _logger.LogError("User not found for valid refresh token. UserId: {UserId}", refreshToken.UserId);
            ThrowError("User not found", 401);
            return;
        }

        // The client sends X-Tenant on refresh, so without a membership check a refresh is a free
        // tenant hop: present a legitimately-issued refresh token with a different X-Tenant and walk
        // out with a token scoped to a tenant you never belonged to. The issuer re-checks every time,
        // which also means a membership revoked mid-session stops working at the next refresh rather
        // than lingering until the refresh token expires.
        var extraClaims = new List<System.Security.Claims.Claim>();
        // Preserve device binding so DeviceTrust enforcement still applies to refreshed tokens.
        if (!string.IsNullOrEmpty(refreshToken.DeviceId))
            extraClaims.Add(new("did", refreshToken.DeviceId));

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
        var newRefreshTokenExpiry = DateTime.UtcNow.AddDays(7);
        
        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = newRefreshTokenString,
            UserId = user.Id,
            ExpiresAt = newRefreshTokenExpiry,
            CreatedAt = DateTime.UtcNow,
            IsRevoked = false,
            // Carried across rotation. Without this the binding survived exactly one refresh: the
            // token being exchanged still had it, the one replacing it did not, so device trust
            // stopped enforcing anything from the second refresh onward and nothing said so.
            DeviceId = refreshToken.DeviceId
        };

        // Revoke old refresh token (rotation)
        refreshToken.IsRevoked = true;
        refreshToken.RevokedReason = "used";
        refreshToken.RevokedAt = DateTime.UtcNow;

        _documentSession.Update(refreshToken);
        _documentSession.Store(newRefreshToken);
        await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.token.refreshed", user.Id, user.Username,
            ipAddress: barakoCMS.Infrastructure.DeviceContext.From(HttpContext).IpAddress, ct: ct);

        try
        {
            // Optimistic-concurrency guard: if another request rotated this same token first,
            // this commit throws and we reject rather than issuing a second valid token.
            await _documentSession.SaveChangesAsync(ct);
        }
        catch (JasperFx.ConcurrencyException)
        {
            _logger.LogWarning(
                "Concurrent refresh-token use detected for UserId: {UserId}. Rejecting duplicate rotation.",
                refreshToken.UserId);
            ThrowError("Refresh token was already used. Please log in again.", 401);
            return;
        }

        _logger.LogInformation(
            "Token refreshed for user: {Username}, UserId: {UserId}",
            user.Username, user.Id);

        // Also in a cookie page script cannot read. The body still carries it for
        // non-browser callers; see RefreshTokenCookie for why this is an addition.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Set(HttpContext, newRefreshTokenString, newRefreshTokenExpiry);

        await Send.ResponseAsync(new Response
        {
            Token = jwtToken,
            Expiry = accessTokenExpiry,
            RefreshToken = newRefreshTokenString,
            RefreshTokenExpiry = newRefreshTokenExpiry
        });
    }
}
