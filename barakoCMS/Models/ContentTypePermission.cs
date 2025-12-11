namespace barakoCMS.Models;

/// <summary>
/// Content Type specific permissions (CRUD operations)
/// </summary>
public class ContentTypePermission
{
    /// <summary>
    /// Content type slug this permission applies to (e.g., "article", "product")
    /// </summary>
    public string ContentTypeSlug { get; set; } = string.Empty;

    /// <summary>
    /// CREATE permission rule
    /// </summary>
    public PermissionRule Create { get; set; } = new();

    /// <summary>
    /// READ permission rule
    /// </summary>
    public PermissionRule Read { get; set; } = new();

    /// <summary>
    /// UPDATE permission rule
    /// </summary>
    public PermissionRule Update { get; set; } = new();

    /// <summary>
    /// DELETE permission rule
    /// </summary>
    public PermissionRule Delete { get; set; } = new();
}
