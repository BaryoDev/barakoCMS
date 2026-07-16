namespace barakoCMS.Models;

/// <summary>
/// A tenant in a multi-tenant deployment, plus its public profile. A global document (not itself
/// tenant-scoped). Single-tenant deployments run under <see cref="DefaultSlug"/>.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>URL-safe public identifier / handle, used to route to the tenant (the path segment).</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }

    // ---- public profile (shown on the tenant's public page) ----
    public string? About { get; set; }

    /// <summary>Human-readable location label, e.g. a city.</summary>
    public string? Location { get; set; }

    /// <summary>Optional absolute URL that opens the location in a maps app.</summary>
    public string? LocationUrl { get; set; }

    /// <summary>A social handle, e.g. "@rckoronadal".</summary>
    public string? SocialHandle { get; set; }

    public string? Email { get; set; }

    /// <summary>Absolute contact URL (typically a Facebook page). Validated on write.</summary>
    public string? ContactUrl { get; set; }

    /// <summary>Freeform branding (theme color, etc.) resolved by the host/UI.</summary>
    public Dictionary<string, string> Branding { get; set; } = new();

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The implicit tenant that existing single-tenant data and non-handle requests use.</summary>
    public const string DefaultSlug = "default";
}
