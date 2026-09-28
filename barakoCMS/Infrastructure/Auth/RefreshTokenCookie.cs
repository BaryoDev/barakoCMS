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
/// Sign-in still returns the refresh token in the body, and so does a refresh that was sent the token
/// in the body. A cookie is a browser mechanism, and the generated clients, module consumers and
/// anything on a phone all read it from the response. A refresh that was sent only the cookie
/// answers with the cookie only: that caller is a browser, and a body is something page script can
/// read.
/// </remarks>
internal static class RefreshTokenCookie
{
    public const string Name = "barako_refresh";

    /// <summary>
    /// Scoped to the two routes that consume it, so it is not attached to every API call. One cookie
    /// per path, since a cookie has one path: logout needs it too, to sign out once the access
    /// token has expired.
    /// </summary>
    private static readonly string[] Paths = ["/api/auth/refresh", "/api/auth/logout"];

    public static void Set(HttpContext http, string refreshToken, DateTime expiresUtc)
    {
        foreach (var path in Paths)
            http.Response.Cookies.Append(Name, refreshToken, Options(http, path, expiresUtc));
    }

    public static void Clear(HttpContext http)
    {
        foreach (var path in Paths)
            http.Response.Cookies.Delete(Name, Options(http, path, DateTime.UtcNow.AddDays(-1)));
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

    private static CookieOptions Options(HttpContext http, string path, DateTime expiresUtc) => new()
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
        // have, and this cookie only goes to the refresh and logout routes, from the app's own code. A
        // cross-origin deployment that needs it can serve both halves from one origin, which the
        // playground already does, or fall back to the token in the body. Lax also keeps the cookie
        // off a POST from another site, so another site cannot sign a user out.
        SameSite = SameSiteMode.Lax,

        Path = path,
        Expires = new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc)),
    };
}
