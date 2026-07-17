using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace BarakoCMS.ExternalAuth;

/// <summary>Shared helpers for the OAuth redirect flows (base URL + the short-lived CSRF/state cookie).</summary>
public static class ExternalAuthSupport
{
    /// <summary>The public base URL of the app (App:BaseUrl, else the request's scheme+host).</summary>
    public static string BaseUrl(IConfiguration config, HttpContext ctx) =>
        (config["App:BaseUrl"] ?? $"{ctx.Request.Scheme}://{ctx.Request.Host}").TrimEnd('/');

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
