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
}
