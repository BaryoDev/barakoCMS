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

    // Field-level sensitivity. When not Public, the field is masked for callers who are not
    // SuperAdmin and not in VisibleToRoles (falling back to a default role policy when that list
    // is empty). See SensitivityService.
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Public;
    public List<string> VisibleToRoles { get; set; } = new();
    public FieldMask Mask { get; set; } = FieldMask.Default;
}

/// <summary>How a masked field is presented to callers who may not see it.</summary>
public enum FieldMask
{
    Default, // Remove for Hidden fields, Redact for Sensitive fields
    Remove,  // drop the key entirely
    Redact,  // replace the value with "***"
    Last4,   // keep only the last 4 characters, e.g. "***-**-6789"
}

/// <summary>Global sensitivity enforcement mode (config: Sensitivity:Mode).</summary>
public enum SensitivityMode
{
    Off,           // no scrubbing at all
    SensitiveOnly, // scrub only fields/documents marked Sensitive or Hidden (default)
    All,           // reserved: strict lockdown (currently behaves as SensitiveOnly)
}
