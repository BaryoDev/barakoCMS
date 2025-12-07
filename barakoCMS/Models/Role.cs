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
    /// When this role was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
