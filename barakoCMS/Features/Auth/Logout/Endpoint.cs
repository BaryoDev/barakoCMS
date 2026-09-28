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
        Options(x => x.RequireRateLimiting("auth"));
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
    /// Revokes every refresh token the cookie's user holds, the same as a bearer logout. Any stored
    /// token counts, revoked or expired included, since a caller holding one is that session. A
    /// cookie that matches nothing gets the same answer as one that matched, so this route cannot be
    /// used to test whether a token is live.
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
        if (token != null)
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
