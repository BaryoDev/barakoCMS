using System.Collections.Generic;

namespace barakoCMS.Models;

public class ContentTypeDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty; // e.g., "post", "product"
    public string DisplayName { get; set; } = string.Empty; // e.g., "Blog Post"
    public string Description { get; set; } = string.Empty;
    public List<FieldDefinition> Fields { get; set; } = new();

    /// <summary>
    /// Whether this type is served by the anonymous public delivery API (<c>/api/public/{type}</c>,
    /// its search and slug routes, and the RSS feed).
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so. Delivery used to be opt-out: any type was served as long
    /// as the content was Published with Public sensitivity, which are the defaults for documents and
    /// fields alike. Modelling members or a ledger as content therefore produced an anonymous endpoint
    /// for them that nobody asked for — and on a live deployment it did exactly that.
    ///
    /// Publishing is a decision, so it has to be made explicitly. Field-level sensitivity still
    /// applies on top of this: opting a type in never implies every field on it is public.
    /// </remarks>
    public bool IsPubliclyDeliverable { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class FieldDefinition
{
    public string Name { get; set; } = string.Empty; // e.g., "title", "sku"
    public string DisplayName { get; set; } = string.Empty;
    // Field type. The accepted set lives in FieldTypeRegistry (the single source of
    // truth both validators read from): string/text, int, decimal, money, bool,
    // date/datetime, time, email, url, slug, uuid, richtext, markdown, json, array,
    // object, reference. (blob is still planned, not yet accepted.)
    public string Type { get; set; } = "text";

    /// <summary>For a <c>reference</c> field, the content type its value points at.</summary>
    /// <remarks>
    /// Required for a reference and meaningless for anything else. Without it a reference is an
    /// untyped uuid: nothing can validate what it points at, delivery cannot resolve it, and the
    /// admin cannot offer a picker. That is what storing a bare <c>uuid</c> already gives you, and
    /// it is the thing this field type exists to stop being the only option.
    /// </remarks>
    public string? ReferenceType { get; set; }
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
