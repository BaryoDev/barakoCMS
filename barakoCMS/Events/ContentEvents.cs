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
