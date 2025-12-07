namespace barakoCMS.Models;

/// <summary>
/// User group for organizing users (supports nested groups in future)
/// </summary>
public class UserGroup
{
    /// <summary>
    /// Unique identifier
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Group name (e.g., "Marketing Team", "Engineering")
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Group description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// User IDs in this group
    /// </summary>
    public List<Guid> UserIds { get; set; } = new();

    /// <summary>
    /// Parent group ID (for nested groups - future feature)
    /// </summary>
    public Guid? ParentGroupId { get; set; }

    /// <summary>
    /// Child group IDs (for nested groups - future feature)
    /// </summary>
    public List<Guid> ChildGroupIds { get; set; } = new();
}
