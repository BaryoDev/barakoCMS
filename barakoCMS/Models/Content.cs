namespace barakoCMS.Models;

public enum ContentStatus
{
    Draft,
    Published,
    Archived
}

public class Content
{
    public Guid Id { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = new();
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Versioning is handled by Marten, but we can track who updated it
    public Guid LastModifiedBy { get; set; }

    public void Apply(barakoCMS.Events.ContentCreated @event)
    {
        Id = @event.Id;
        ContentType = @event.ContentType;
        Data = @event.Data;
        Status = @event.Status;
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.CreatedBy;
    }

    public void Apply(barakoCMS.Events.ContentUpdated @event)
    {
        Data = @event.Data;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.UpdatedBy;
    }

    public void Apply(barakoCMS.Events.ContentStatusChanged @event)
    {
        Status = @event.NewStatus;
        UpdatedAt = DateTime.UtcNow;
        LastModifiedBy = @event.UpdatedBy;
    }
}
