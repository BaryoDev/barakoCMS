using FastEndpoints;
using barakoCMS.Infrastructure.Services;
using System.IdentityModel.Tokens.Jwt;

namespace barakoCMS.Features.Auth.Logout;

public class Endpoint : EndpointWithoutRequest<Response>
{
    private readonly ITokenRevocationService _revocationService;
    private readonly ILogger<Endpoint> _logger;

    public Endpoint(
        ITokenRevocationService revocationService,
        ILogger<Endpoint> logger)
    {
        _revocationService = revocationService;
        _logger = logger;
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
            await SendUnauthorizedAsync(ct);
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

        _logger.LogInformation("User logged out: UserId={UserId}", userId);

        await SendAsync(new Response
        {
            Message = "Successfully logged out"
        });
    }
}
