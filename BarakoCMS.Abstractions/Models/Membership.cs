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

    /// <summary>
    /// What this tenant says about the member, for example their branch or ward. A permission
    /// condition reads a value as <c>$CURRENT_USER.&lt;name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// A value here decides which rows its holder may read or change, so it is written only through
    /// the member endpoints, by a caller who may already assign roles in the tenant. Names are case
    /// sensitive. A name that is absent, or whose value is empty, matches nothing.
    /// </remarks>
    public Dictionary<string, string> Profile { get; set; } = new();
}

public enum MembershipStatus
{
    Active,
    Suspended,
    Removed,
}
