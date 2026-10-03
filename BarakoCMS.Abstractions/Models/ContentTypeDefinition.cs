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

    /// <summary>
    /// Whether this type holds exactly one entry, so it can model a site's own values: address,
    /// phone, opening hours, footer text.
    /// </summary>
    /// <remarks>
    /// Off by default, so no type that exists today changes. Enforced on create, in
    /// <c>ContentValidatorService</c>: a second entry is refused. Editing the entry that is already
    /// there is not creating one and stays allowed, which is what makes the flag usable at all, and
    /// it also means turning this on for a type that somehow has two entries does not make either of
    /// them read only.
    ///
    /// The count behind the cap takes every entry whatever its status, drafts and archived ones
    /// included, because a reader that takes the first item of the list cannot tell an archived row
    /// from a live one and two rows is the ambiguity the flag exists to remove. So archiving the one
    /// entry does not free the slot: that takes <c>DELETE /api/contents/{id}/erase</c>, which needs
    /// SuperAdmin and the <c>EraseContent</c> capability.
    ///
    /// A Portability bundle import is not subject to the cap. It validates nothing by design, and it
    /// runs during a restore or a migration, where dropping rows that the bundle holds loses content
    /// at the worst possible moment. A bundle carrying two entries of a singleton type lands both.
    ///
    /// <c>SystemSetting</c> is not a substitute: it is deployment level with fixed categories,
    /// neither per tenant nor public.
    /// </remarks>
    public bool IsSingleton { get; set; }

    /// <summary>
    /// Where an entry of this type lives on the site, as a path holding <c>{slug}</c>, for example
    /// <c>/blog/{slug}</c>.
    /// </summary>
    /// <remarks>
    /// Null, the default, is every type stored before this existed: the feed and the sitemap then
    /// read <c>Feeds:Paths:{type}</c> from configuration and fall back to <c>/{type}/{slug}</c>, as
    /// they always did. Set, it is read ahead of both, so the path is the editor's to change and
    /// not the operator's. A path only: it starts with <c>/</c>, has no empty segment and no
    /// <c>.</c> or <c>..</c> segment, and is joined to the site URL the deployment configures, so
    /// it cannot name another host.
    /// </remarks>
    public string? RouteTemplate { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>This type's own states, or null to use Draft, Published and Archived.</summary>
    /// <remarks>
    /// Null is the common case and must stay cheap: every existing content type has none, and a type
    /// without one has to behave exactly as it did before this existed.
    /// </remarks>
    public LifecycleDefinition? Lifecycle { get; set; }

    /// <summary>Values only one entry of this type may hold at a time, or null for none.</summary>
    /// <remarks>
    /// Null is every type stored before this existed, and a type with none is written exactly as it
    /// was. Each rule is checked inside the entry write, under a lock on the values, so two writes
    /// at once cannot both pass. See <see cref="UniquenessRule"/>.
    /// </remarks>
    public List<UniquenessRule>? Uniqueness { get; set; }
}

/// <summary>
/// One set of fields whose values, taken together, only one entry of a type may hold.
/// </summary>
/// <remarks>
/// Values are compared the way PostgreSQL compares <c>jsonb</c>: text exactly, with case and
/// spaces counting, numbers by value so 1 and 1.0 are the same, and text never equal to a number.
/// An entry with no value in one of the fields (missing, null or the empty string) is outside the
/// rule, as a row holding NULL is outside a unique index.
/// </remarks>
public class UniquenessRule
{
    /// <summary>What the rule is called, for example "OneOpenEntryPerTeacher". Unique within a type.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The fields compared, each a field of the type or <see cref="CreatedByField"/>.
    /// </summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>
    /// The lifecycle state an entry has to be in for the rule to count it, or null to count every
    /// entry.
    /// </summary>
    /// <remarks>
    /// An entry that leaves the state frees its values. An entry with no state yet is read as being
    /// in the type's initial state, which is how a transition reads it.
    /// </remarks>
    public string? WhenState { get; set; }

    /// <summary>The name that stands for <see cref="Content.CreatedBy"/> in <see cref="Fields"/>.</summary>
    public const string CreatedByField = "$createdBy";
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

    /// <summary>
    /// Fields of the type that must be sent with this move, for example a rejection reason on
    /// "Reject". Empty for a transition that requires nothing.
    /// </summary>
    /// <remarks>
    /// A value already on the entry does not count: the value has to come with the move. A field
    /// named here may be written by whoever may perform the transition, with the move, without the
    /// update permission: that is what lets a reviewer who may not edit an entry say why they
    /// rejected it.
    /// </remarks>
    public List<string> RequiredFields { get; set; } = new();

    /// <summary>
    /// Fields of the type that may be sent with this move and need not be, for example a note
    /// beside the rejection reason.
    /// </summary>
    public List<string> OptionalFields { get; set; } = new();
}

public class FieldDefinition
{
    public string Name { get; set; } = string.Empty; // e.g., "title", "sku"
    public string DisplayName { get; set; } = string.Empty;
    // Field type. The accepted set lives in FieldTypeRegistry (the single source of
    // truth both validators read from): string/text, int, decimal, money, bool,
    // date/datetime, time, email, url, slug, uuid, richtext, markdown, json, array,
    // object, reference, geopoint, choice, file.
    public string Type { get; set; } = "text";

    /// <summary>For a <c>reference</c> field, the content type its value points at.</summary>
    /// <remarks>
    /// Required for a reference and meaningless for anything else. Without it a reference is an
    /// untyped uuid: nothing can validate what it points at, delivery cannot resolve it, and the
    /// admin cannot offer a picker. That is what storing a bare <c>uuid</c> already gives you, and
    /// it is the thing this field type exists to stop being the only option.
    /// </remarks>
    public string? ReferenceType { get; set; }

    /// <summary>For a <c>choice</c> field, the options it accepts, in display order.</summary>
    /// <remarks>
    /// Required for a choice and refused on anything else. The value is what an entry stores, what
    /// delivery returns and what a filter matches; the label is what an editor sees. Keeping the two
    /// apart is what lets a label be reworded without touching an entry, and gives a renderer a stable
    /// key to hang a colour on. Null for every other type, so definitions that exist today read back
    /// exactly as they did.
    /// </remarks>
    public List<FieldOption>? Options { get; set; }

    /// <summary>
    /// For a <c>choice</c> or <c>reference</c> field, whether an entry holds a list of options or
    /// ids rather than one.
    /// </summary>
    /// <remarks>
    /// Refused on any other type. A many-valued reference holds at most 100 ids, each naming an
    /// entry of <see cref="ReferenceType"/>, with no id twice.
    /// </remarks>
    public bool Multiple { get; set; }

    /// <summary>
    /// For a <c>money</c> field, the ISO 4217 code every amount in it is in, for example <c>USD</c>.
    /// </summary>
    /// <remarks>
    /// Null, the default, leaves the field a plain number, which is what every money field stored
    /// before this existed is. Set, an entry write refuses an amount with more decimal places than
    /// <see cref="Scale"/> allows, and refuses text or a number a decimal cannot hold exactly.
    /// Nothing is rounded. The amount is stored and returned as a plain JSON number either way, so
    /// the currency is read from here and not from the entry. Three capital letters, and refused on
    /// a field of any other type.
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// For a <c>money</c> field with a <see cref="Currency"/>, the most decimal places an amount may
    /// carry, from 0 to 8.
    /// </summary>
    /// <remarks>
    /// Null takes the currency's own minor unit: 2 for USD, 0 for JPY, 3 for KWD. Required for a
    /// code the built-in list does not hold, and refused without a currency.
    /// </remarks>
    public int? Scale { get; set; }

    /// <summary>
    /// The editor a console should open for this field: <c>blocks</c>, <c>menu</c>, <c>links</c> or
    /// <c>image</c>.
    /// </summary>
    /// <remarks>
    /// Null, the default, is every field stored before this existed, and a console then picks an
    /// editor the way it did before, from the field's type and name. A hint, and nothing the API
    /// enforces on an entry: the field's type still decides what a value may be. Lower case, one of
    /// the names <c>GET /api/meta/describe</c> lists under <c>fieldEditors</c>, and only on a field
    /// type that editor can hold.
    /// </remarks>
    public string? Editor { get; set; }

    /// <summary>The group this field sits in on a generated edit screen, for example "Branding".</summary>
    /// <remarks>
    /// Null is a field in no section. Sections are compared exactly, case included, and appear in
    /// the order of the first field that names each. Fields keep the type's own order inside one.
    /// </remarks>
    public string? Section { get; set; }

    /// <summary>
    /// What this field is to the entry: <c>title</c>, <c>summary</c> or <c>date</c>.
    /// </summary>
    /// <remarks>
    /// Null, the default, leaves the feed and the SEO block finding these by field name, as they did
    /// before roles existed. Set, the field is read ahead of those names, which stay the fallback
    /// when it holds nothing. One field per role in a type.
    /// </remarks>
    public string? Role { get; set; }

    public bool IsRequired { get; set; }
    public object? DefaultValue { get; set; }
    public Dictionary<string, object> ValidationRules { get; set; } = new(); // min, max, regex, etc.

    // Field-level sensitivity. When not Public, the field is masked for callers who are not
    // SuperAdmin and hold none of the roles in VisibleToRoles. When that list is empty the field
    // is open to a role holding view_sensitive or view_hidden, whichever its level asks for.
    // See SensitivityService.
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Public;

    /// <summary>
    /// The roles that may see the field while it is not Public, as role ids written as text.
    /// </summary>
    /// <remarks>
    /// An entry that is not an id is a role name: what a definition stored before ids were holds,
    /// and what a write keeps for a name no role carries. It matches a role of exactly that name.
    /// </remarks>
    public List<string> VisibleToRoles { get; set; } = new();
    public FieldMask Mask { get; set; } = FieldMask.Default;
}

/// <summary>One option a <c>choice</c> field accepts.</summary>
public class FieldOption
{
    /// <summary>What an entry stores. Matched exactly, case included.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>What an editor sees. A consumer shows the value when this is empty.</summary>
    public string Label { get; set; } = string.Empty;
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
