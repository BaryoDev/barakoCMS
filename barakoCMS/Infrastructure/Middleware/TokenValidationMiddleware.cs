using System.IdentityModel.Tokens.Jwt;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Middleware to validate JWT tokens against the revocation blacklist.
/// Runs after authentication to check if the token has been revoked.
/// </summary>
public class TokenValidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TokenValidationMiddleware> _logger;

    public TokenValidationMiddleware(
        RequestDelegate next,
        ILogger<TokenValidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ITokenRevocationService revocationService)
    {
        // Only check authenticated requests
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var jtiClaim = context.User.FindFirst(JwtRegisteredClaimNames.Jti);
            
            if (jtiClaim != null)
            {
                var jti = jtiClaim.Value;
                var isRevoked = await revocationService.IsTokenRevokedAsync(jti, context.RequestAborted);

                if (isRevoked)
                {
                    _logger.LogWarning(
                        "Revoked token attempted to access {Path}. JTI: {Jti}",
                        context.Request.Path, jti);

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Token has been revoked",
                        message = "This token is no longer valid. Please log in again."
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}
