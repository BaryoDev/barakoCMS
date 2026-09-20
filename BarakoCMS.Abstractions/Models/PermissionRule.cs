namespace barakoCMS.Models;

/// <summary>
/// Permission rule defining access control with optional conditions
/// </summary>
public class PermissionRule
{
    /// <summary>
    /// Whether this permission is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Optional conditions that must be met for permission to apply
    /// Format: Directus/Strapi style - { "field": { "operator": "value" } }
    /// Example: { "author": { "_eq": "$CURRENT_USER" } }
    /// </summary>
    public Dictionary<string, object>? Conditions { get; set; }
}
