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

    /// <summary>
    /// Permission per named transition, for a content type that declares its own lifecycle.
    /// Keyed by transition name.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Update"/>, and deliberately not implied by it. Whoever may edit an
    /// invoice was also whoever could approve it, so separation of duties could not be expressed at
    /// all, and it is the first thing an auditor asks about.
    ///
    /// They are genuinely different questions. A clerk edits and may not approve. A manager approves
    /// and may not edit the amount being approved. Neither is reachable from CRUD.
    ///
    /// **Undeclared means refused.** The tempting shortcut is to let an ungoverned transition fall
    /// back to Update so existing configurations keep working, and it would silently grant approval
    /// to everyone who can edit, which is the defect being fixed rather than a migration path.
    ///
    /// The OrdinalIgnoreCase comparer below applies only to a dictionary built in memory. It does
    /// not survive persistence: System.Text.Json constructs a fresh Dictionary with the default
    /// comparer when Marten deserialises the role. PermissionResolver therefore compares the key
    /// itself rather than relying on this, and matching has to stay case insensitive because the
    /// lifecycle matches a transition name that way too.
    /// </remarks>
    public Dictionary<string, PermissionRule> Transitions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
