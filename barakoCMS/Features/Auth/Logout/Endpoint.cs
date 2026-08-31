using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Logout;

internal class Endpoint : EndpointWithoutRequest<Response>
{
    private readonly ITokenRevocationService _revocationService;
    private readonly ILogger<Endpoint> _logger;
    private readonly IDocumentSession _documentSession;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public Endpoint(
        ITokenRevocationService revocationService,
        ILogger<Endpoint> logger,
        IDocumentSession documentSession,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _revocationService = revocationService;
        _logger = logger;
        _documentSession = documentSession;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/auth/logout");
        // Require authentication - user must be logged in to log out
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        // Extract JTI from current token
        var jtiClaim = User.FindFirst(JwtRegisteredClaimNames.Jti);
        var userIdClaim = User.FindFirst("UserId");

        if (jtiClaim == null || userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
        {
            _logger.LogWarning("Logout attempt with invalid token claims");
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var jti = jtiClaim.Value;

        // Get token expiry from claims
        var expClaim = User.FindFirst(JwtRegisteredClaimNames.Exp);
        DateTime expiry = DateTime.UtcNow.AddMinutes(15); // Default fallback
        
        if (expClaim != null && long.TryParse(expClaim.Value, out var expUnix))
        {
            expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
        }

        // Revoke the access token
        await _revocationService.RevokeTokenAsync(jti, userId, "logout", expiry, ct);

        // Revoke all refresh tokens for the user
        await _revocationService.RevokeAllUserTokensAsync(userId, "logout", ct);

        var device = barakoCMS.Infrastructure.DeviceContext.From(HttpContext);
        await AuditLog.RecordAsync(_documentSession, _tenant.Slug, "auth.logout", userId, User.FindFirst("Username")?.Value,
            ipAddress: device.IpAddress, ct: ct);
        await _documentSession.SaveChangesAsync(ct);

        _logger.LogInformation("User logged out: UserId={UserId}", userId);

        // Signing out clears the cookie too, or the browser keeps presenting a refresh token the
        // server has already revoked and the next refresh is a 401 nobody can explain.
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.Clear(HttpContext);

        await Send.ResponseAsync(new Response
        {
            Message = "Successfully logged out"
        });
    }
}
