using System.Text.Json.Serialization;

namespace barakoCMS.Events;

/// <summary>
/// A content event that says when the change happened, as opposed to when it was recorded.
/// </summary>
/// <remarks>
/// Two clocks answer that question and they are not the same. The writer stamps
/// <see cref="OccurredAt"/> once, as it applies the event to the document. Marten stamps the
/// transaction time when the event commits, and a replay can only see that one, so a rebuilt
/// document had timestamps that differed from the original by the write latency. For an audit trail
/// that is not acceptable, and it is not a flaw in the test that found it.
///
/// Domain time drives the projection; storage time drives ordering. The separation matters on a
/// multi-instance deployment, where application clocks skew and the database clock does not.
///
/// <b>Zero means unstated.</b> An event written before 4.0 has no such field, so it deserialises to
/// <c>default</c>. A projection must fall back to the Marten timestamp for those rather than
/// rebuilding them at year one, which is what <c>ContentProjection.OccurredAt</c> does.
/// </remarks>
public interface IContentEvent
{
    /// <summary>When the change happened. <c>default</c> on an event written before 4.0.</summary>
    DateTime OccurredAt { get; }
}

[method: JsonConstructor]
public record ContentCreated(
    Guid Id,
    string ContentType,
    Dictionary<string, object> Data,
    Models.ContentStatus Status,
    Guid CreatedBy,
    string? SearchText,
    Models.SensitivityLevel Sensitivity,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentCreated(
        Guid id,
        string contentType,
        Dictionary<string, object> data,
        Models.ContentStatus status,
        Guid createdBy,
        string? searchText,
        Models.SensitivityLevel sensitivity)
        : this(id, contentType, data, status, createdBy, searchText, sensitivity, DateTime.UtcNow)
    {
    }
}

[method: JsonConstructor]
public record ContentUpdated(
    Guid Id,
    Dictionary<string, object> Data,
    Guid UpdatedBy,
    string? SearchText,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentUpdated(Guid id, Dictionary<string, object> data, Guid updatedBy, string? searchText)
        : this(id, data, updatedBy, searchText, DateTime.UtcNow)
    {
    }
}

// Marked because the obsolete constructor below gives this record two, and the serializer will not
// choose between them. It needed no attribute while there was only one.
[method: JsonConstructor]
public record ContentStatusChanged(
    Guid Id,
    Models.ContentStatus NewStatus,
    Guid UpdatedBy,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentStatusChanged(Guid id, Models.ContentStatus newStatus, Guid updatedBy)
        : this(id, newStatus, updatedBy, DateTime.UtcNow)
    {
    }
}

/// <summary>
/// Publication scheduling changed for a content item.
/// </summary>
/// <remarks>
/// Scheduling used to be written straight to the document with no event, so the audit trail said
/// nothing about who scheduled what, and anything reconstructing state from the stream would lose
/// both dates.
/// </remarks>
[method: JsonConstructor]
public record ContentScheduled(
    Guid Id,
    DateTime? ScheduledPublishAt,
    DateTime? ScheduledUnpublishAt,
    Guid UpdatedBy,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentScheduled(
        Guid id, DateTime? scheduledPublishAt, DateTime? scheduledUnpublishAt, Guid updatedBy)
        : this(id, scheduledPublishAt, scheduledUnpublishAt, updatedBy, DateTime.UtcNow)
    {
    }
}

/// <summary>
/// Document-level sensitivity changed for a content item.
/// </summary>
/// <remarks>
/// Sensitivity drives field-level redaction, so state rebuilt without it produces a record that
/// looks correct and is readable by roles that should not see it. That is why it is carried
/// explicitly rather than inferred.
/// </remarks>
[method: JsonConstructor]
public record ContentSensitivityChanged(
    Guid Id,
    Models.SensitivityLevel Sensitivity,
    Guid UpdatedBy,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentSensitivityChanged(Guid id, Models.SensitivityLevel sensitivity, Guid updatedBy)
        : this(id, sensitivity, updatedBy, DateTime.UtcNow)
    {
    }
}

/// <summary>
/// An entry moved through a named transition in its content type's own lifecycle.
/// </summary>
/// <remarks>
/// Separate from <see cref="ContentStatusChanged"/> rather than an extension of it. That one carries
/// a <see cref="Models.ContentStatus"/>, which is the core's three states and decides whether public
/// delivery serves an entry. This carries the type's own states, which decide nothing about
/// delivery. Folding them together would make the delivery question unanswerable without knowing
/// which kind of change it was.
///
/// The transition name is recorded as well as the states, because it is what a permission and a
/// workflow key on. From and To are recorded so a replay does not have to consult the content type
/// definition, which can change after the fact.
/// </remarks>
[method: JsonConstructor]
public record ContentTransitioned(
    Guid Id,
    string Transition,
    string FromState,
    string ToState,
    Guid UpdatedBy,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentTransitioned(
        Guid id, string transition, string fromState, string toState, Guid updatedBy)
        : this(id, transition, fromState, toState, updatedBy, DateTime.UtcNow)
    {
    }
}

/// A field's sensitivity changed on the content type, so this entry's derived
/// <see cref="Models.Content.SearchText"/> was rebuilt.
/// </summary>
/// <remarks>
/// Appended once per affected entry rather than once against the type, because SearchText lives on
/// the entry and is carried by its events. Scrubbing it with a plain store would hold only until the
/// next projection rebuild, which replays the last ContentCreated or ContentUpdated and writes the
/// old text back: a field taken out of anonymous search would quietly return to it, and nothing
/// about the rebuild would look wrong.
///
/// Both levels travel with it so the stream says why the text changed rather than only that it did.
/// </remarks>
[method: JsonConstructor]
public record ContentFieldSensitivityChanged(
    Guid Id,
    string Field,
    Models.SensitivityLevel From,
    Models.SensitivityLevel To,
    string? SearchText,
    Guid ChangedBy,
    DateTime OccurredAt) : IContentEvent
{
    /// <summary>The shape before <see cref="OccurredAt"/>, kept so existing callers compile.</summary>
    [Obsolete("Pass OccurredAt, so a rebuild reproduces the timestamps. Removal planned for barakoCMS 5.0.")]
    public ContentFieldSensitivityChanged(
        Guid id,
        string field,
        Models.SensitivityLevel from,
        Models.SensitivityLevel to,
        string? searchText,
        Guid changedBy)
        : this(id, field, from, to, searchText, changedBy, DateTime.UtcNow)
    {
    }
}
