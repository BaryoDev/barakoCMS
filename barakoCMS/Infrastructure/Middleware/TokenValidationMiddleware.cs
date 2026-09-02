using System.IdentityModel.Tokens.Jwt;
using barakoCMS.Infrastructure.Services;

namespace barakoCMS.Infrastructure.Middleware;

/// <summary>
/// Refuses tokens that have been revoked by id, and tokens issued before their user's sessions were
/// invalidated. Runs after authentication.
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

            if (await IsBeforeSessionEpochAsync(context, sessionEpoch))
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

        await _next(context);
    }

    /// <summary>
    /// Whether this token was issued before its user's sessions were invalidated.
    /// </summary>
    /// <remarks>
    /// Every branch that cannot answer returns false, which serves the request. That direction is
    /// deliberate and it is the opposite of the usual advice, so it is worth the paragraph.
    ///
    /// This runs on every authenticated request. A check that refuses when it cannot read the
    /// database locks out every user of the instance at once, for a database blip, and the failure
    /// looks like a total outage. Serving instead degrades to the behaviour that shipped for years:
    /// the refresh token is still revoked, so the exposure is bounded by the access token's own
    /// fifteen minute lifetime. A worse answer than this check working, and a much better one than
    /// everybody locked out.
    ///
    /// The precision handling is the fiddly part and it is not clock skew, which is what it looks
    /// like. <c>iat</c> is whole seconds; the epoch is a timestamp with sub-second precision. So a
    /// token issued 50ms AFTER a bump at 12:00:00.900 carries iat 12:00:00, which is less than the
    /// epoch, and a naive comparison refuses a token minted after the event it is meant to survive.
    /// That is a sign-in that silently does not work.
    ///
    /// Truncating the epoch to whole seconds fixes that exactly, without an arbitrary allowance:
    /// both sides are then the same unit. A token from the same second as the bump is served, one
    /// from any earlier second is refused, and the residual window is under one second rather than
    /// the fifteen minutes this closes.
    ///
    /// A first version used a five second allowance instead, and it was too blunt to notice: it also
    /// served tokens issued four seconds BEFORE the change, which is precisely the case being shut.
    /// </remarks>

    private static async Task<bool> IsBeforeSessionEpochAsync(HttpContext context, ISessionEpochService sessionEpoch)
    {
        var userIdClaim = context.User.FindFirst("UserId")?.Value;
        if (!Guid.TryParse(userIdClaim, out var userId))
            return false;

        var iatClaim = context.User.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
        if (!long.TryParse(iatClaim, out var iatSeconds))
            return false;

        DateTime? validFrom;
        try
        {
            validFrom = await sessionEpoch.ValidFromAsync(userId, context.RequestAborted);
        }
        catch (Exception ex)
        {
            // Logged, not swallowed silently. This catch exists so a database blip cannot lock out
            // every user at once, and that is worth keeping, but a control that stops working must
            // say so. While writing this the cache threw on every call and the silent catch turned
            // the whole feature into a no-op that looked like it worked.
            context.RequestServices
                .GetRequiredService<ILogger<TokenValidationMiddleware>>()
                .LogError(ex, "Session epoch check failed; serving the request. Access tokens are only bounded by their own lifetime until this is fixed.");
            return false;
        }

        // Null is the common case: nothing has ever invalidated this user's sessions.
        if (validFrom is not { } epoch)
            return false;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(iatSeconds).UtcDateTime;
        var epochSecond = new DateTime(
            epoch.Ticks - (epoch.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

        return issuedAt < epochSecond;
    }
}
