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
    /// A value from the caller's member profile: { "Branch": { "_eq": "$CURRENT_USER.branch" } }
    /// </summary>
    public Dictionary<string, object>? Conditions { get; set; }

    /// <summary>
    /// On a Read rule, the fields an entry this rule grants shows. Null shows every field.
    /// </summary>
    /// <remarks>
    /// It narrows what field sensitivity allows and never widens it: a Sensitive field named here
    /// is still masked for a caller sensitivity masks it for. Across the caller's roles the sets of
    /// the rules that grant an entry are joined, and a granting rule with no set shows every field.
    /// </remarks>
    public List<string>? ReadableFields { get; set; }

    /// <summary>
    /// On a Create or Update rule, the fields this rule lets the caller set. Null lets every field
    /// the caller may read be set.
    /// </summary>
    public List<string>? WritableFields { get; set; }
}
