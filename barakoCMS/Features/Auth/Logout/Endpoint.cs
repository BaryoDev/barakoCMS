using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Logout;

internal class Endpoint(
    ITokenRevocationService revocationService,
    ILogger<Endpoint> logger,
    IDocumentSession documentSession,
    barakoCMS.Infrastructure.Multitenancy.TenantContext tenant) : EndpointWithoutRequest<Response>
{
    public override void Configure()
    {
        Post("/api/auth/logout");
        // Anonymous so the refresh cookie alone can sign out once the access token has expired.
        // Without a valid bearer or a refresh cookie the handler still answers 401.
        AllowAnonymous();
        // Its own bucket, not the auth one: signing out must not be refused because sign-ins and
        // refreshes from the same address used up the password-guessing limit.
        Options(x => x.RequireRateLimiting(barakoCMS.Infrastructure.Security.RateLimitSetup.LogoutPolicy));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jtiClaim = User.FindFirst(JwtRegisteredClaimNames.Jti);
        var userIdClaim = User.FindFirst("UserId");

        if (jtiClaim == null || userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            await LogoutWithCookieAsync(ct);
            return;
        }

        var jti = jtiClaim.Value;

        var expClaim = User.FindFirst(JwtRegisteredClaimNames.Exp);
        DateTime expiry = DateTime.UtcNow.AddMinutes(15); // Default fallback
        
        if (expClaim != null && long.TryParse(expClaim.Value, out var expUnix))
        {
            expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
        }

        await revocationService.RevokeTokenAsync(jti, userId, "logout", expiry, ct);

        await revocationService.RevokeAllUserTokensAsync(userId, "logout", ct);

        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        await AuditLog.RecordAsync(documentSession, tenant.Slug, "auth.logout", userId, User.FindFirst("Username")?.Value,
            ipAddress: device.IpAddress, ct: ct);
        await documentSession.SaveChangesAsync(ct);

        logger.LogInformation("User logged out: UserId={UserId}", userId);

        // Signing out clears the cookie too, or the browser keeps presenting a refresh token the
        // server has already revoked and the next refresh is a 401 nobody can explain.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Clear(HttpContext);

        await Send.ResponseAsync(new Response
        {
            Message = "Successfully logged out"
        });
    }

    /// <summary>
    /// Signs out with the refresh cookie when there is no usable bearer.
    /// </summary>
    /// <remarks>
    /// A live cookie revokes every refresh token its user holds, the same as a bearer logout. A
    /// revoked or expired one revokes nothing: rotated tokens stay stored until cleanup, and letting
    /// one end the session that replaced it would let an old copy sign the user out again and again.
    /// Every cookie gets the same answer and a cleared cookie, so this route cannot be used to test
    /// whether a token is live.
    /// </remarks>
    private async Task LogoutWithCookieAsync(CancellationToken ct)
    {
        var presented = barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Read(HttpContext);
        if (presented == null)
        {
            logger.LogWarning("Logout attempt with no bearer and no refresh cookie");
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var token = await barakoCMS.Infrastructure.Auth.RefreshTokenLookup.FindAsync(documentSession, presented, ct);
        if (token is { IsRevoked: false } && token.ExpiresAt > DateTime.UtcNow)
        {
            await revocationService.RevokeAllUserTokensAsync(token.UserId, "logout", ct);
            await AuditLog.RecordAsync(documentSession, tenant.Slug, "auth.logout", token.UserId, null,
                ipAddress: barakoCMS.Infrastructure.DeviceContext.From(HttpContext).IpAddress, ct: ct);
            await documentSession.SaveChangesAsync(ct);
            logger.LogInformation("User logged out with the refresh cookie: UserId={UserId}", token.UserId);
        }

        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Clear(HttpContext);
        await Send.ResponseAsync(new Response { Message = "Successfully logged out" });
    }
}
