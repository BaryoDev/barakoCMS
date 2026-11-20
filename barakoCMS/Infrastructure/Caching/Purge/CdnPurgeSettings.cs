namespace barakoCMS.Infrastructure.Caching.Purge;

/// <summary>
/// One tenant's CDN purge configuration. One document per tenant, off by default.
/// </summary>
/// <remarks>
/// Single tenanted with the tenant as data and an id derived from the slug, the same as
/// <see cref="barakoCMS.Models.EmailSettings"/>, so the resolver can read any tenant outside a
/// request. The token lives only in <see cref="ProtectedToken"/>, encrypted. No plaintext field
/// holds it. The default tenant has no row: it reads host configuration.
/// </remarks>
internal sealed class CdnPurgeSettings
{
    public Guid Id { get; set; }

    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>One of <see cref="CdnPurgeProvider"/>.</summary>
    public string Provider { get; set; } = CdnPurgeProvider.None;

    public bool Enabled { get; set; }

    /// <summary>The Cloudflare zone id or the Fastly service id.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>The API token, encrypted with <c>ISecretProtector</c>. Never the plaintext.</summary>
    public string ProtectedToken { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    public static Guid IdForTenant(string tenantSlug)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("cdn-purge-settings:" + tenantSlug.Trim().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }
}
