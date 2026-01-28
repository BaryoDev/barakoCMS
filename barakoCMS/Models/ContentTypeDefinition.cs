using System.Collections.Generic;

namespace barakoCMS.Models;

public class ContentTypeDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty; // e.g., "post", "product"
    public string DisplayName { get; set; } = string.Empty; // e.g., "Blog Post"
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class FieldDefinition
{
    public string Name { get; set; } = string.Empty; // e.g., "title", "sku"
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = "text"; // text, number, date, boolean, richtext, reference
    public bool IsRequired { get; set; }
    public object? DefaultValue { get; set; }
    public Dictionary<string, object> ValidationRules { get; set; } = new(); // min, max, regex, etc.
    public FieldSensitivity? Sensitivity { get; set; } // Field-level visibility settings
}

/// <summary>
/// Defines visibility/sensitivity settings for a field.
/// Controls who can see the field and how it's handled for unauthorized users.
/// </summary>
public class FieldSensitivity
{
    /// <summary>
    /// The action to take when user is not authorized to view this field.
    /// "Remove" - Field is completely removed from response
    /// "Mask" - Field value is replaced with MaskValue
    /// </summary>
    public FieldSensitivityAction Action { get; set; } = FieldSensitivityAction.Remove;

    /// <summary>
    /// List of roles that are allowed to see this field's actual value.
    /// If empty, only SuperAdmin can see the field.
    /// </summary>
    public List<string> AllowedRoles { get; set; } = new();

    /// <summary>
    /// The value to display when masking. Defaults to "***".
    /// Only used when Action is Mask.
    /// </summary>
    public string MaskValue { get; set; } = "***";
}

/// <summary>
/// Action to take when a user is not authorized to view a sensitive field.
/// </summary>
public enum FieldSensitivityAction
{
    /// <summary>
    /// Completely remove the field from the response.
    /// </summary>
    Remove,

    /// <summary>
    /// Replace the field value with a mask (e.g., "***").
    /// </summary>
    Mask
}
