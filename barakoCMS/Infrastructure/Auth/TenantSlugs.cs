using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

internal static class TenantSlugs
{
    /// <summary>The default slug for null or blank, else the slug trimmed and lowercased.</summary>
    public static string Normalise(string? slug) =>
        string.IsNullOrWhiteSpace(slug) ? Tenant.DefaultSlug : slug.Trim().ToLowerInvariant();
}
