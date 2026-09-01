using System.Text.Json.Serialization;

namespace barakoCMS.Events;

[method: JsonConstructor]
public record ContentCreated(
    Guid Id,
    string ContentType,
    Dictionary<string, object> Data,
    Models.ContentStatus Status,
    Guid CreatedBy,
    string? SearchText,
    Models.SensitivityLevel Sensitivity);

[method: JsonConstructor]
public record ContentUpdated(
    Guid Id,
    Dictionary<string, object> Data,
    Guid UpdatedBy,
    string? SearchText);

public record ContentStatusChanged(Guid Id, Models.ContentStatus NewStatus, Guid UpdatedBy);

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
    Guid UpdatedBy);

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
    Guid UpdatedBy);

/// <summary>
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
    Guid ChangedBy);
