namespace barakoCMS.Features.Content.History;

internal class Request : barakoCMS.Models.ListRequest
{
    public Guid Id { get; set; }
}

/// <summary>
/// One event from a content stream, whatever kind of change it recorded.
/// </summary>
/// <remarks>
/// The mapper used to build these from ContentCreated and ContentUpdated only and return null for
/// everything else, and the nulls were dropped, so publishing a document left no trace in the
/// document's own history. Every event now produces an entry and <see cref="ChangeType"/> says
/// which kind it was, so a client can tell a status change from a document version.
///
/// Only create and update carry a document, so <see cref="Data"/> is null on the rest rather than
/// an empty dictionary pretending the change was a version with no fields.
/// </remarks>
internal class VersionResponse
{
    public Guid Id { get; set; }

    /// <summary>Name of the event this entry records, for example <c>ContentStatusChanged</c>.</summary>
    public string ChangeType { get; set; } = string.Empty;

    public Dictionary<string, object>? Data { get; set; }
    public Guid LastModifiedBy { get; set; }
    public Guid VersionId { get; set; }

    // Always UTC. Marten hands back the event's timestamp in the server's local offset, and the
    // same instant written two ways in one API is how a client ends up with two date parsers.
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Set by the events that carry a status: creation and a status change.</summary>
    public barakoCMS.Models.ContentStatus? Status { get; set; }

    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? ScheduledUnpublishAt { get; set; }

    /// <summary>Set by the events that carry a sensitivity level: creation and a sensitivity change.</summary>
    public barakoCMS.Models.SensitivityLevel? Sensitivity { get; set; }
}

// The history used to come back as {versions: [...]}. It is a collection like any other and now
// uses the same envelope, so a client can page a long-lived document's history.

/// <summary>The content after a rollback, in the shape the rest of the API describes content in.</summary>
/// <remarks>
/// Rollback returned the stored <c>Content</c> document. That put <c>SearchText</c>, a derived field
/// nobody asks for, on the wire, and meant any property added to the document shipped to every
/// caller the moment it was stored. <c>CreatedBy</c> arriving in 4.0 did exactly that, silently.
///
/// Deliberately the same shape as <c>Features/Content/Get</c> rather than a sixth spelling of a
/// content item. It is a slice-local type rather than a shared one because that is the house style,
/// and the duplication is the price of slices not reaching into each other.
/// </remarks>
internal class RollbackResponse
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public barakoCMS.Models.ContentStatus Status { get; set; }
    public Guid LastModifiedBy { get; set; }
    public barakoCMS.Models.SensitivityLevel Sensitivity { get; set; }

    public static RollbackResponse From(barakoCMS.Models.Content c) => new()
    {
        Id = c.Id,
        ContentType = c.ContentType,
        Data = c.Data,
        CreatedAt = new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)),
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(c.UpdatedAt, DateTimeKind.Utc)),
        Status = c.Status,
        LastModifiedBy = c.LastModifiedBy,
        Sensitivity = c.Sensitivity,
    };
}
