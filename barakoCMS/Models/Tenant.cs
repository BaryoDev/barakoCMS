namespace barakoCMS.Models;

/// <summary>
/// A tenant in a multi-tenant deployment. A global document (not itself tenant-scoped). Single-tenant
/// deployments run entirely under <see cref="DefaultSlug"/>, so nothing changes for them.
/// </summary>
public class Tenant
{
    public Guid Id { get; set; }

    /// <summary>URL-safe identifier, used as the subdomain.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }

    /// <summary>Freeform branding (theme color, etc.) resolved by the host/UI.</summary>
    public Dictionary<string, string> Branding { get; set; } = new();

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The implicit tenant that existing single-tenant data and non-subdomain requests use.</summary>
    public const string DefaultSlug = "default";
}
