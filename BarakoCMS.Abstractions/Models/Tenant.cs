namespace barakoCMS.Models;

/// <summary>
/// A tenant in a multi-tenant deployment: what routing and administration need. A global document
/// (not itself tenant-scoped). Single-tenant deployments run under <see cref="DefaultSlug"/>.
/// </summary>
/// <remarks>
/// The public profile a tenant used to carry here lives in the tenant's <c>site</c> entry, where it
/// grows by adding a field instead of a property. The obsolete members below are still read from a
/// document stored before <c>migrations/4.6.0/tenant-profile-to-site.sql</c> moved its values, and
/// nothing writes them any more.
/// </remarks>
public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>URL-safe public identifier / handle, used to route to the tenant (the path segment).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [Obsolete("Moved to the Logo field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? LogoUrl { get; set; }

    [Obsolete("Moved to the About field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? About { get; set; }

    /// <summary>Human-readable location label, e.g. a city.</summary>
    [Obsolete("Moved to the Location field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? Location { get; set; }

    /// <summary>Optional absolute URL that opens the location in a maps app.</summary>
    [Obsolete("Moved to the LocationUrl field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? LocationUrl { get; set; }

    /// <summary>A social handle, e.g. "@acmeclub".</summary>
    [Obsolete("Moved to the SocialHandle field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? SocialHandle { get; set; }

    [Obsolete("Moved to the Email field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? Email { get; set; }

    /// <summary>Absolute contact URL.</summary>
    [Obsolete("Moved to the ContactUrl field of the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public string? ContactUrl { get; set; }

    /// <summary>Freeform branding (theme color, etc.) resolved by the host/UI.</summary>
    [Obsolete("A site's colours and theme belong in the tenant's site entry. Removal planned for barakoCMS 6.0.")]
    public Dictionary<string, string> Branding { get; set; } = new();

    /// <summary>
    /// Domains this tenant answers on, so a client can use their own address rather than a
    /// subdomain. A domain belongs to exactly one tenant.
    /// </summary>
    /// <remarks>
    /// Store the bare host, e.g. "abc.com". A leading "www." is ignored on both sides of the match,
    /// so listing it separately is unnecessary and listing only "www.abc.com" still matches
    /// "abc.com". Empty for a tenant reached by subdomain or by the X-Tenant header.
    /// </remarks>
    public List<string> Domains { get; set; } = new();

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The implicit tenant that existing single-tenant data and non-handle requests use.</summary>
    public const string DefaultSlug = "default";
}
