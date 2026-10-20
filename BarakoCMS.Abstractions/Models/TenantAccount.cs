namespace barakoCMS.Models;

/// <summary>
/// An account that owns tenants, for an agency or reseller running tenants for others. A global
/// document (not tenant-scoped).
/// </summary>
/// <remarks>
/// Every tenant belongs to an account. A tenant whose <see cref="Tenant.AccountId"/> is null belongs
/// to the implicit default account, <see cref="DefaultId"/>, which needs no stored document: a
/// deployment that never creates an account behaves as it did before accounts existed. The default
/// account gets a stored document only when someone sets its branding.
/// </remarks>
public class TenantAccount
{
    /// <summary>The id of the implicit account that holds every tenant not assigned elsewhere.</summary>
    public static readonly Guid DefaultId = new("00000000-0000-0000-0000-0000000a0001");

    /// <summary>The slug of the default account.</summary>
    public const string DefaultSlug = "default";

    public Guid Id { get; set; }

    /// <summary>URL-safe identifier, unique across accounts.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The name the console shows in place of its own.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute http or https URL of the logo the console shows.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Console colours by name, for example <c>primary</c> and <c>accent</c>, as CSS colour values.</summary>
    public Dictionary<string, string> Colours { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Lets one user manage the tenants of one account, and no others. The account-scoped form of the
/// global <c>manage_tenants</c> capability. A global document (not tenant-scoped).
/// </summary>
public class TenantAccountManager
{
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    public Guid UserId { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;
}
