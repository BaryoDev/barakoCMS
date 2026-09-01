using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetFieldSensitivity;

internal class Request
{
    /// <summary>The level the field should carry from now on.</summary>
    public SensitivityLevel Sensitivity { get; set; }

    /// <summary>
    /// Roles allowed to read the field while it is not Public. Empty falls back to the default
    /// policy for the level (HR for Sensitive, SuperAdmin only for Hidden).
    /// </summary>
    /// <remarks>
    /// Replaced with the level rather than carried over from the level being left behind. A list
    /// written for Sensitive is not a decision about who may read a Hidden field, and leaving it in
    /// place would silently reinstate an old allowlist the next time somebody raised the level.
    /// </remarks>
    public List<string>? VisibleToRoles { get; set; }

    /// <summary>How the field is presented to a caller who may not read it.</summary>
    public FieldMask? Mask { get; set; }

    /// <summary>
    /// Required to lower a field's sensitivity, and ignored in the other direction.
    /// </summary>
    /// <remarks>
    /// Lowering is retroactive: every value already written under the old level becomes readable to
    /// everyone who can read the type, and anonymously too if the type is publicly deliverable. That
    /// is a disclosure decision, so the request has to say it is one. The refusal names how many
    /// entries are affected, which is the number worth seeing before the second attempt.
    /// </remarks>
    public bool AcknowledgeDisclosure { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;
    public SensitivityLevel From { get; set; }
    public SensitivityLevel To { get; set; }
    public List<string> VisibleToRoles { get; set; } = new();
    public FieldMask Mask { get; set; }

    /// <summary>
    /// How many entries had their derived search text rewritten. Entries that were never indexed,
    /// and entries whose text did not change, are not counted.
    /// </summary>
    public int EntriesReindexed { get; set; }
}
