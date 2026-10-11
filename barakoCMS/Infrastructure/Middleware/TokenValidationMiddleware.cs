using System.IdentityModel.Tokens.Jwt;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Infrastructure.Logging;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Refuses tokens that have been revoked by id, and tokens issued before their user's sessions were
/// invalidated. Runs after authentication.
/// </summary>
public class TokenValidationMiddleware(RequestDelegate next, ILogger<TokenValidationMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        ITokenRevocationService revocationService,
        ISessionEpochService sessionEpoch)
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
                    logger.LogWarning(
                        "Revoked token attempted to access {Path}. JTI: {Jti}",
                        LogSafe.Value(context.Request.Path), jti);

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Token has been revoked",
                        message = "This token is no longer valid. Please log in again."
                    });
                    return;
                }
            }

            if (await SessionEpochCheck.IsBeforeSessionEpochAsync(
                    context.User, sessionEpoch, logger, context.RequestAborted))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Token has been revoked",
                    message = "This token is no longer valid. Please log in again.",
                });
                return;
            }
        }

        await next(context);
    }
}
