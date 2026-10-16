namespace barakoCMS.Models;

/// <summary>
/// Role defining a set of permissions
/// </summary>
public class Role
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Role name (e.g., "Content Editor", "HR Manager")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Role description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Permissions per content type
    /// </summary>
    public List<ContentTypePermission> Permissions { get; set; } = new();

    /// <summary>
    /// System-wide capabilities (e.g., "manage_users", "view_analytics")
    /// </summary>
    public List<string> SystemCapabilities { get; set; } = new();

    /// <summary>
    /// Users holding this role must have MFA turned on. Until they do, their token can only reach
    /// the MFA setup endpoints.
    /// </summary>
    public bool RequireMfa { get; set; }

    /// <summary>
    /// The tenant that owns this role, by slug. Null for a platform role. A tenant's roles are
    /// listed, assigned and edited inside that tenant only, and their permissions name that
    /// tenant's content types.
    /// </summary>
    public string? TenantSlug { get; set; }

    /// <summary>
    /// A role seeded for the whole deployment (SuperAdmin, Admin, User, and a role a module seeds
    /// under a fixed id). Every tenant can assign it, and only a holder of
    /// <see cref="SystemCapabilities.ManagePlatformRoles"/> can change it.
    /// </summary>
    public bool IsPlatform { get; set; }

    /// <summary>
    /// An invite may grant this role. Only a role marked here can be handed out by a holder of
    /// <see cref="SystemCapabilities.InviteTenantMembers"/>.
    /// </summary>
    public bool Invitable { get; set; }

    /// <summary>
    /// When this role was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
