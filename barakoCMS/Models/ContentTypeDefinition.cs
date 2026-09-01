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

    /// <summary>This type's own states, or null to use Draft, Published and Archived.</summary>
    /// <remarks>
    /// Null is the common case and must stay cheap: every existing content type has none, and a type
    /// without one has to behave exactly as it did before this existed.
    /// </remarks>
    public LifecycleDefinition? Lifecycle { get; set; }
}

/// <summary>
/// The states a content type's entries move through, and the named moves between them.
/// </summary>
/// <remarks>
/// <see cref="ContentStatus"/> is Draft, Published, Archived, in the core, for every type. That is
/// right for a blog post and wrong for an invoice, which is Draft, Submitted, Approved, Sent, Paid.
///
/// A type that declares no lifecycle keeps the three it has always had, so nothing existing changes.
/// Declaring one does not replace <c>ContentStatus</c>: the enum keeps meaning what it means to the
/// delivery API, which is whether the public sees an entry, and a custom state is carried alongside
/// in <see cref="Content.LifecycleState"/>. Conflating "approved" with "published" is the shortcut
/// that produces a system nobody can explain, and it is also load bearing: the enum is public API
/// and <c>mt_doc_contents_idx_status</c> indexes it as an integer.
/// </remarks>
public class LifecycleDefinition
{
    /// <summary>Every state an entry of this type may be in. Order is display order.</summary>
    public List<string> States { get; set; } = new();

    /// <summary>The state a new entry starts in. Must be one of <see cref="States"/>.</summary>
    public string InitialState { get; set; } = string.Empty;

    /// <summary>The moves that are allowed, each with a name.</summary>
    public List<StateTransition> Transitions { get; set; } = new();
}

/// <summary>One allowed move between two states.</summary>
/// <remarks>
/// Named, rather than an arbitrary assignment of a new state. "Set state to Approved" and "Approve"
/// are the same edit and different events, and only the second can be governed: the name is what a
/// permission attaches to and what a workflow triggers on. An interface that only took a target
/// state could express neither.
/// </remarks>
public class StateTransition
{
    /// <summary>What the move is called, for example "Approve". Unique within a type.</summary>
    public string Name { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
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
