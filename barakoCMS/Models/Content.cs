namespace barakoCMS.Models;

/// <summary>Where an entry is in the publishing lifecycle.</summary>
/// <remarks>
/// Numbered explicitly, and the numbers are load bearing. Marten's serializer has no
/// JsonStringEnumConverter (the one in ServiceCollectionExtensions is the HTTP serializer), so these
/// are stored as integers in the data column and in two indexes. Inserting a member rather than
/// appending one would silently redefine every existing row: every Archived entry in every deployed
/// database would read back as whatever took its number.
/// </remarks>
public enum ContentStatus
{
    Draft = 0,
    Published = 1,
    Archived = 2,

    /// <summary>
    /// A draft with a publish time set, waiting for the sweeper to promote it.
    /// </summary>
    /// <remarks>
    /// A real status rather than a condition derived from <see cref="Content.ScheduledPublishAt"/>,
    /// per DECISIONS.md D12. Derived would have kept the write path untouched, at the cost of the
    /// lifecycle lying: an entry that is going to publish on Friday is not a draft, and a filter for
    /// it had to be written out again everywhere anybody wanted one.
    ///
    /// Only the publish side. A Published entry carrying a future unpublish time stays Published,
    /// because it is published; the pending change does not un-publish it in the meantime.
    /// </remarks>
    Scheduled = 3,
}

public enum SensitivityLevel
{
    Public,
    Sensitive,
    Hidden
}

public class Content
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Public;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Scheduling. The scheduler promotes a Draft to Published at/after ScheduledPublishAt, and
    // Archives a Published item at/after ScheduledUnpublishAt. Setting either emits ContentScheduled
    // and each transition emits a real ContentStatusChanged, so workflows fire, history stays correct
    // and a rebuild recovers both dates. The consumed field is cleared through an event too, which is
    // why clearing is visible in the stream despite happening with no user behind it. Both are UTC.
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? ScheduledUnpublishAt { get; set; }

    // Versioning is handled by Marten, but we can track who updated it
    public Guid LastModifiedBy { get; set; }

    /// <summary>
    /// The entry's state within its content type's own lifecycle, or null when the type declares none.
    /// </summary>
    /// <remarks>
    /// Alongside <see cref="Status"/>, not instead of it. The enum still decides whether the public
    /// delivery API serves this entry, and this decides where it sits in a workflow that the type
    /// defined for itself. A type with no lifecycle leaves this null forever.
    /// </remarks>
    public string? LifecycleState { get; set; }

    /// <summary>Who created this. Set once, from the event, and never from a request body.</summary>
    /// <remarks>
    /// Distinct from <see cref="LastModifiedBy"/>, which moves on every edit. Ownership has to
    /// survive somebody else editing the record, so "who owns this" and "who touched it last" cannot
    /// be the same field. They were, until 4.0: <c>ContentCreated</c> has always carried
    /// <c>CreatedBy</c> and <c>Apply</c> wrote it into <see cref="LastModifiedBy"/>, where the first
    /// update overwrote it.
    ///
    /// Because the events carried it all along, content written before 4.0 is not ownerless: a stream
    /// rebuild recovers the value. Until one runs, an existing document reads <c>Guid.Empty</c>, and
    /// an ownership condition denies it rather than granting it, which is the safe direction.
    /// </remarks>
    public Guid CreatedBy { get; set; }

    // Derived public search text used for full-text search.
    public string? SearchText { get; set; }

    /// <summary>
    /// Applies an event to this document.
    /// </summary>
    /// <remarks>
    /// These are the projection. <paramref name="occurredAt"/> is passed in rather than read from
    /// the clock because a rebuild replays events long after they happened: reading UtcNow here
    /// would stamp every document with the time of the rebuild instead of the time of the change,
    /// and nothing about the result would look wrong.
    /// </remarks>
    public void Apply(barakoCMS.Events.ContentCreated @event, DateTime occurredAt)
    {
        Id = @event.Id;
        ContentType = @event.ContentType;
        Data = @event.Data;
        Status = @event.Status;
        Sensitivity = @event.Sensitivity;
        CreatedAt = occurredAt;
        UpdatedAt = occurredAt;
        CreatedBy = @event.CreatedBy;
        LastModifiedBy = @event.CreatedBy;
        SearchText = @event.SearchText;
    }

    public void Apply(barakoCMS.Events.ContentUpdated @event, DateTime occurredAt)
    {
        Data = @event.Data;
        UpdatedAt = occurredAt;
        LastModifiedBy = @event.UpdatedBy;
        SearchText = @event.SearchText;
    }

    public void Apply(barakoCMS.Events.ContentStatusChanged @event, DateTime occurredAt)
    {
        Status = @event.NewStatus;
        UpdatedAt = occurredAt;
        LastModifiedBy = @event.UpdatedBy;
    }

    public void Apply(barakoCMS.Events.ContentScheduled @event, DateTime occurredAt)
    {
        ScheduledPublishAt = @event.ScheduledPublishAt;
        ScheduledUnpublishAt = @event.ScheduledUnpublishAt;
        UpdatedAt = occurredAt;
        LastModifiedBy = @event.UpdatedBy;
    }

    public void Apply(barakoCMS.Events.ContentTransitioned @event, DateTime occurredAt)
    {
        LifecycleState = @event.ToState;
        UpdatedAt = occurredAt;
        LastModifiedBy = @event.UpdatedBy;
    }

    public void Apply(barakoCMS.Events.ContentSensitivityChanged @event, DateTime occurredAt)
    {
        Sensitivity = @event.Sensitivity;
        UpdatedAt = occurredAt;
        LastModifiedBy = @event.UpdatedBy;
    }

    /// <summary>Rebuilds only the derived search text.</summary>
    /// <remarks>
    /// The one Apply that leaves <see cref="UpdatedAt"/> and <see cref="LastModifiedBy"/> alone, and
    /// that is the point of it. The entry was not edited: a schema decision changed which part of it
    /// is public. Stamping it would move every entry of the type to the top of any "recently
    /// updated" list, change what the sitemap reports as lastmod for all of them at once, and name an
    /// administrator as the last person to touch content they never opened.
    ///
    /// <paramref name="occurredAt"/> is taken and unused for that reason, not by oversight.
    /// </remarks>
    public void Apply(barakoCMS.Events.ContentFieldSensitivityChanged @event, DateTime occurredAt)
    {
        SearchText = @event.SearchText;
    }

    // The single-argument forms these replace. Kept because Content ships in the BarakoCMS package
    // and external code compiles against it. Each delegates with UtcNow, which is what the old body
    // read, so a caller that has not moved across behaves exactly as before. A rebuild must not use
    // these: replaying an old event with the current clock is the failure the two-argument forms
    // exist to prevent.

    [Obsolete("Use Apply(ContentCreated, DateTime). Removal planned for barakoCMS 5.0.")]
    public void Apply(barakoCMS.Events.ContentCreated @event) => Apply(@event, DateTime.UtcNow);

    [Obsolete("Use Apply(ContentUpdated, DateTime). Removal planned for barakoCMS 5.0.")]
    public void Apply(barakoCMS.Events.ContentUpdated @event) => Apply(@event, DateTime.UtcNow);

    [Obsolete("Use Apply(ContentStatusChanged, DateTime). Removal planned for barakoCMS 5.0.")]
    public void Apply(barakoCMS.Events.ContentStatusChanged @event) => Apply(@event, DateTime.UtcNow);
}
