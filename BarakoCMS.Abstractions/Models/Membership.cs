namespace barakoCMS.Models;

/// <summary>
/// Links a global <see cref="User"/> to a <see cref="Tenant"/> with the roles they hold in that
/// tenant. Per-tenant roles live here: the same user can hold different roles in different tenants.
/// A global document (not tenant-scoped).
/// </summary>
public class Membership
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The tenant this membership is in (matches <see cref="Tenant.Slug"/>).</summary>
    public string TenantSlug { get; set; } = string.Empty;

    /// <summary>Roles held within this tenant (role ids are scoped to the tenant).</summary>
    public List<Guid> RoleIds { get; set; } = new();

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
}

public enum MembershipStatus
{
    Active,
    Suspended,
    Removed,
}
