using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Whether the refresh cookie goes out marked Secure.</summary>
    /// <remarks>
    /// Its own method so it can be tested without standing up a host. Deciding it end to end turned
    /// out to depend on the order tests build their hosts in, and a flaky assertion about a security
    /// attribute is worse than none.
    /// </remarks>
    internal static bool IsSecure(HttpContext http) =>
        !http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();

    private static CookieOptions Options(HttpContext http, DateTime expiresUtc) => new()
    {
        HttpOnly = true,

        // Secure everywhere except a Development host, rather than following Request.IsHttps.
        //
        // IsHttps describes the hop that reached this process, not the one the browser made. Behind
        // a TLS-terminating ingress that is not forwarding headers, a request the user made over
        // https arrives here as http, and the refresh cookie would ship without Secure on exactly
        // the deployment that most needs it. "Production is https" was the assumption, and the
        // proxy is where it stops being true.
        //
        // Development is still exempt, because a cookie marked Secure is not sent over http and
        // every local stack would break with a symptom that looks like "refresh does not work"
        // rather than like a cookie policy.
        Secure = IsSecure(http),

        // Lax, not None. None requires Secure and therefore https, which the local stacks do not
        // have, and this cookie is only ever sent to the refresh route by the app's own code. A
        // cross-origin deployment that needs it can serve both halves from one origin, which the
        // playground already does, or fall back to the token in the body.
        SameSite = SameSiteMode.Lax,

        Path = Path,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)),
    };
}
