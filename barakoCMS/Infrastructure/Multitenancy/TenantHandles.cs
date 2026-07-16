using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>Validation for tenant handles (the public id / URL segment) and public-profile URLs.</summary>
public static class TenantHandles
{
    /// <summary>Handles that would collide with app routes and therefore can't be claimed by a tenant.</summary>
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "logout", "signin", "signout", "api", "admin", "health", "metrics",
        "dashboard", "me", "tenant", "tenants", "static", "assets", "public",
        "_next", "www", "app", "default", "favicon.ico", "robots.txt",
    };

    // lowercase alphanumerics and hyphens, 3-40 chars, no leading/trailing hyphen.
    private static readonly Regex Format = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", RegexOptions.Compiled);

    public static bool IsValidHandle(string? handle) =>
        !string.IsNullOrWhiteSpace(handle) &&
        Format.IsMatch(handle) &&
        !Reserved.Contains(handle);

    public static bool IsValidAbsoluteUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
