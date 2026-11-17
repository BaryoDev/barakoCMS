using System.Buffers.Text;
using System.Security.Cryptography;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.ExternalAuth;

/// <summary>Shared helpers for the OAuth redirect flows (base URL + the short-lived CSRF/state cookie).</summary>
public static class ExternalAuthSupport
{
    /// <summary>
    /// The public base URL of the app, which every provider's <c>redirect_uri</c> is built from.
    /// </summary>
    /// <remarks>
    /// <c>App:BaseUrl</c>, else the request's scheme and host but only where <c>AllowedHosts</c>
    /// makes that host trustworthy. This used to take the <c>Host</c> header unconditionally, which
    /// let a caller put their own domain into the redirect_uri of an authorization request. The
    /// providers reject a redirect_uri they have not been given, so nothing was known to be
    /// exploitable, but that control belongs to Google and GitHub rather than to us.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Nothing configured a base URL and the host is not constrained.</exception>
    public static string BaseUrl(IConfiguration config, HttpContext ctx) =>
        barakoCMS.Infrastructure.Security.CanonicalHost.BaseUrl(config, ctx.Request)
        ?? throw new barakoCMS.Infrastructure.Security.BaseUrlNotConfiguredException(
            barakoCMS.Infrastructure.Security.CanonicalHost.BaseUrlKey);

    /// <summary>
    /// A fresh OAuth <c>state</c>: 32 bytes from <see cref="RandomNumberGenerator"/>, base64url without
    /// padding, so it is safe in a query string and a cookie without escaping.
    /// </summary>
    internal static string NewState() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// The start of a provider's redirect flow: refuses (null) when the provider is off or not allowed
    /// for the club, otherwise sets the state and club cookies, binds the device and returns both for
    /// the authorization URL. The cookie names are <c>{cookiePrefix}_state</c> and <c>{cookiePrefix}_club</c>.
    /// </summary>
    internal static async Task<(string State, string Club)?> BeginAsync(
        HttpContext http, IConfiguration config, IQuerySession session, string? clubQuery,
        string cookiePrefix, string provider, string section, string idKey, CancellationToken ct)
    {
        if (!ProviderEnabled(config, section, idKey)) return null;
        var club = (clubQuery ?? "").Trim().ToLowerInvariant();
        if (!await ExternalProviders.AllowedAsync(session, club, provider, ct)) return null;
        var state = NewState();
        http.Response.Cookies.Append($"{cookiePrefix}_state", state, ShortCookie());
        http.Response.Cookies.Append($"{cookiePrefix}_club", club, ShortCookie());
        SocialHandoff.BindDevice(http, config);
        return (state, club);
    }

    /// <summary>What a provider's callback learned from the cookies and query before any provider call.</summary>
    /// <param name="Code">The authorization code, or null when it is missing or the state does not match the cookie.</param>
    internal sealed record CallbackState(string BaseUrl, string Club, string? Code);

    /// <summary>
    /// The start of a provider's callback: reads the club and state cookies, clears them, and checks the
    /// returned state against the cookie. Null when the provider is not allowed for the club.
    /// </summary>
    internal static async Task<CallbackState?> ReadCallbackStateAsync(
        HttpContext http, IConfiguration config, IQuerySession session, string? code, string? state,
        string cookiePrefix, string provider, CancellationToken ct)
    {
        var baseUrl = BaseUrl(config, http);
        var club = http.Request.Cookies[$"{cookiePrefix}_club"] ?? "";
        if (!await ExternalProviders.AllowedAsync(session, club, provider, ct)) return null;
        var cookieState = http.Request.Cookies[$"{cookiePrefix}_state"];
        http.Response.Cookies.Delete($"{cookiePrefix}_state");
        http.Response.Cookies.Delete($"{cookiePrefix}_club");
        var valid = !string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(state) && state == cookieState;
        return new CallbackState(baseUrl, club, valid ? code : null);
    }

    /// <summary>A short-lived, HttpOnly, Lax cookie — survives the top-level GET redirect back from the provider.</summary>
    public static CookieOptions ShortCookie() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10),
        Path = "/",
    };

    /// <summary>
    /// Whether all external/social logins are turned off by the master switch
    /// (<c>ExternalAuth:Enabled = false</c>). Overrides every provider at once.
    /// </summary>
    public static bool ExternalAuthDisabled(IConfiguration config) =>
        string.Equals(config["ExternalAuth:Enabled"], "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a provider is on: the master switch must not be off, its client id/secret must be
    /// configured, AND it must not be explicitly disabled (<c>{section}:Enabled = false</c>). Lets a
    /// provider be wired up but kept dark until ready, or all providers be killed with one flag.
    /// </summary>
    public static bool ProviderEnabled(IConfiguration config, string section, string idKey) =>
        !ExternalAuthDisabled(config) &&
        !string.IsNullOrWhiteSpace(config[$"{section}:{idKey}"]) &&
        !string.Equals(config[$"{section}:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
}
