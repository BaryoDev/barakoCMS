namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Carries the refresh token in a cookie page script cannot read.
/// </summary>
/// <remarks>
/// The admin stored both tokens in <c>localStorage</c>, which any script on the origin can read.
/// The access token is a 15 minute credential and has to be readable, because the client sends it
/// as a bearer. The refresh token is the one that matters: seven days, renewable, and rotation does
/// not help an attacker who simply keeps refreshing. One XSS, or one compromised dependency in the
/// admin build, turned into a week of account takeover.
///
/// So the durable credential moves out of script's reach and the short one stays in memory.
///
/// The body still carries the refresh token, deliberately. A cookie is a browser mechanism, and the
/// generated clients, module consumers and anything on a phone all read it from the response. Making
/// this a replacement rather than an addition would break every non-browser caller to fix a
/// browser-only problem. What changes is that the admin stops persisting it, so an XSS arriving
/// after sign-in has nothing to steal.
/// </remarks>
internal static class RefreshTokenCookie
{
    public const string Name = "barako_refresh";

    /// <summary>
    /// Scoped to the one route that consumes it, so it is not attached to every API call.
    /// </summary>
    private const string Path = "/api/auth/refresh";

    public static void Set(HttpContext http, string refreshToken, DateTime expiresUtc)
    {
        http.Response.Cookies.Append(Name, refreshToken, Options(http, expiresUtc));
    }

    public static void Clear(HttpContext http)
    {
        http.Response.Cookies.Delete(Name, Options(http, DateTime.UtcNow.AddDays(-1)));
    }

    /// <summary>The cookie value, or null when the caller did not send one.</summary>
    public static string? Read(HttpContext http) =>
        http.Request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static CookieOptions Options(HttpContext http, DateTime expiresUtc) => new()
    {
        HttpOnly = true,

        // Secure follows the request rather than being hardcoded on. A cookie marked Secure is not
        // sent over http, so hardcoding it would silently break every http development stack, and
        // the failure would look like "refresh does not work" rather than like a cookie policy.
        // Production is https, so this is on where it counts.
        Secure = http.Request.IsHttps,

        // Lax, not None. None requires Secure and therefore https, which the local stacks do not
        // have, and this cookie is only ever sent to the refresh route by the app's own code. A
        // cross-origin deployment that needs it can serve both halves from one origin, which the
        // playground already does, or fall back to the token in the body.
        SameSite = SameSiteMode.Lax,

        Path = Path,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)),
    };
}
