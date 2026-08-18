namespace barakoCMS.Models;

public enum ContentStatus
{
    Draft,
    Published,
    Archived
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

    // Scheduling. Forward-looking intent held on the read model (not the event stream): the scheduler
    // promotes a Draft to Published at/after ScheduledPublishAt, and Archives a Published item at/after
    // ScheduledUnpublishAt. Each transition emits a real ContentStatusChanged event, so workflows fire
    // and history stays correct; the consumed field is then cleared. Both are UTC.
    public DateTime? ScheduledPublishAt { get; set; }
    public DateTime? ScheduledUnpublishAt { get; set; }

    // Versioning is handled by Marten, but we can track who updated it
    public Guid LastModifiedBy { get; set; }

    // Derived public search text used for full-text search.
    public string? SearchText { get; set; }

    public void Apply(barakoCMS.Events.ContentCreated @event)
    {
        Id = @event.Id;
        ContentType = @event.ContentType;
        Data = @event.Data;
        Status = @event.Status;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.CreatedBy;
        SearchText = @event.SearchText;
    }

    public void Apply(barakoCMS.Events.ContentUpdated @event)
    {
        Data = @event.Data;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.UpdatedBy;
        SearchText = @event.SearchText;
    }

    public void Apply(barakoCMS.Events.ContentStatusChanged @event)
    {
        Status = @event.NewStatus;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.UpdatedBy;
    }

}
