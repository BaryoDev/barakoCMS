namespace barakoCMS.Models;

/// <summary>
/// A named machine identity for integrations, so a key does not have to act as the person who made
/// it. It is not a <see cref="User"/> and has no login, no password and no membership. Its roles
/// decide what a key bound to it can do, and the key keeps working when the administrator who made
/// it leaves the tenant. Global (single-tenanted) like the other auth documents, with the tenant it
/// belongs to stored on the row.
/// </summary>
public class ServicePrincipal
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string TenantSlug { get; set; } = Tenant.DefaultSlug;

    public string Name { get; set; } = string.Empty;

    /// <summary>The roles the principal holds in its tenant. Never a platform role.</summary>
    public List<Guid> RoleIds { get; set; } = new();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The user who made it. Recorded for the audit trail only; it grants nothing.</summary>
    public Guid CreatedBy { get; set; }

    /// <summary>Set when disabled. A disabled principal's keys are refused on their next request.</summary>
    public DateTime? DisabledAt { get; set; }

    public bool Disabled => DisabledAt is not null;
}
