namespace barakoCMS.Events;

public record ContentCreated(Guid Id, string ContentType, Dictionary<string, object> Data, Models.ContentStatus Status, Guid CreatedBy, string? SearchText)
{
    [Obsolete("Use the constructor that includes SearchText. This overload will be removed in the next major version.")]
    public ContentCreated(
        Guid Id,
        string ContentType,
        Dictionary<string, object> Data,
        Models.ContentStatus Status,
        Guid CreatedBy)
        : this(Id, ContentType, Data, Status, CreatedBy, null)
    {
    }
}

public record ContentUpdated(Guid Id, Dictionary<string, object> Data, Guid UpdatedBy, string? SearchText)
{
    [Obsolete("Use the constructor that includes SearchText. This overload will be removed in the next major version.")]
    public ContentUpdated(
        Guid Id,
        Dictionary<string, object> Data,
        Guid UpdatedBy)
        : this(Id, Data, UpdatedBy, null)
    {
    }
}


public record ContentStatusChanged(Guid Id, Models.ContentStatus NewStatus, Guid UpdatedBy);
