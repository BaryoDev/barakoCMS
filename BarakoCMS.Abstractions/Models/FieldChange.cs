namespace barakoCMS.Models;

/// <summary>What a field change does to a type's entries.</summary>
public enum FieldChangeKind
{
    /// <summary>The field takes a new name, and every entry's value moves to it.</summary>
    Rename = 0,

    /// <summary>The field takes a new type, and every entry's value is converted through a mapping.</summary>
    Convert = 1,

    /// <summary>The field leaves the type, and every entry's value with it.</summary>
    Remove = 2,

    /// <summary>The field's sensitivity changes, and every entry's search text is rebuilt for it.</summary>
    Sensitivity = 3,
}

/// <summary>Where a field change is.</summary>
public enum FieldChangeState
{
    /// <summary>Queued or part way. Entry writes to the type wait for it.</summary>
    Running = 0,

    /// <summary>Every step is done.</summary>
    Completed = 1,
}

/// <summary>
/// A change to one field of a content type that touches every entry of the type, applied by a job
/// in batches, with its progress stored so a run that stops part way can be resumed.
/// </summary>
/// <remarks>
/// One per change, in the tenant of the type. The steps run in <see cref="Step"/> order and each
/// batch commits together with the progress it made, so a batch is either done and recorded or
/// neither. While a change is <see cref="FieldChangeState.Running"/>, entry writes to the type are
/// refused: a write landing between two batches could otherwise leave an entry holding the old
/// shape after the job has passed it.
/// </remarks>
public sealed class FieldChange
{
    public Guid Id { get; set; }

    public string ContentType { get; set; } = string.Empty;

    /// <summary>The field as the type named it when the change was asked for.</summary>
    public string Field { get; set; } = string.Empty;

    public FieldChangeKind Kind { get; set; }

    /// <summary>For a rename, the field's new name.</summary>
    public string? NewName { get; set; }

    /// <summary>For a convert, the field's new type.</summary>
    public string? ToType { get; set; }

    /// <summary>For a convert to a choice, the options the field takes.</summary>
    public List<FieldOption>? Options { get; set; }

    /// <summary>For a convert, stored values, as text, and what each becomes.</summary>
    public Dictionary<string, string>? Mapping { get; set; }

    /// <summary>For a sensitivity change, the level the field was at.</summary>
    public SensitivityLevel? FromSensitivity { get; set; }

    /// <summary>For a sensitivity change, the level the field goes to.</summary>
    public SensitivityLevel? ToSensitivity { get; set; }

    /// <summary>For a sensitivity change, the roles that may see the field at its new level.</summary>
    public List<string>? VisibleToRoles { get; set; }

    /// <summary>For a sensitivity change, how the field is masked at its new level.</summary>
    public FieldMask? Mask { get; set; }

    /// <summary>
    /// For a sensitivity change, whether the definition changes before the search text is rebuilt:
    /// true when lowering, false when raising, so whichever step exposes less goes first.
    /// </summary>
    public bool DefinitionFirst { get; set; }

    public FieldChangeState State { get; set; }

    /// <summary>The step the change is on, from 0. What each step does depends on the kind.</summary>
    public int Step { get; set; }

    /// <summary>The last entry id the current step finished, or null at the start of a step.</summary>
    public Guid? Cursor { get; set; }

    /// <summary>Entries the change has rewritten so far, across its steps.</summary>
    public int Processed { get; set; }

    /// <summary>Entries of the type when the change was asked for.</summary>
    public int Total { get; set; }

    /// <summary>Entries whose value a convert or a remove dropped, counted when the change was asked for.</summary>
    public int ValuesLost { get; set; }

    /// <summary>The last failure's exception type and message, cleared by the next batch that commits.</summary>
    public string? Error { get; set; }

    public Guid RequestedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
