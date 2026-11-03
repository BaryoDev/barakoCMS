namespace barakoCMS.Models;

/// <summary>
/// One tenant's overrides for the settings modules declare (#896). One document per tenant.
/// </summary>
/// <remarks>
/// Single tenanted with the tenant as data, and an id derived from the slug, the same as the
/// per-tenant <see cref="EmailSettings"/> row. A provider has to read another tenant's overrides
/// outside a request (a queued send runs for a tenant the current scope does not belong to), and a
/// conjoined document would only be visible under that tenant's own session.
///
/// Secrets live in <see cref="ProtectedValues"/>, encrypted. No plaintext field holds one.
/// </remarks>
public class TenantSettings
{
    public Guid Id { get; set; }

    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>Plain values, keyed <c>{section}:{key}</c> in lower case.</summary>
    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>Secret values encrypted with <c>ISecretProtector</c>, keyed like <see cref="Values"/>.</summary>
    public Dictionary<string, string> ProtectedValues { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    public static Guid IdForTenant(string tenantSlug)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("tenant-settings:" + tenantSlug.Trim().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    public static string ValueKey(string section, string key) =>
        $"{section.Trim().ToLowerInvariant()}:{key.Trim().ToLowerInvariant()}";
}
