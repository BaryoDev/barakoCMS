namespace barakoCMS.Events;

public record ContentCreated(
    Guid Id,
    string ContentType,
    Dictionary<string, object> Data,
    Models.ContentStatus Status,
    Guid CreatedBy,
    string? SearchText)
{
    [Obsolete("Use the six-value constructor. Removal planned for the next major version.")]
    public ContentCreated(
        Guid id,
        string contentType,
        Dictionary<string, object> data,
        Models.ContentStatus status,
        Guid createdBy)
        : this(id, contentType, data, status, createdBy, null)
    {
    }

    [Obsolete("Use the six-value Deconstruct overload. Removal planned for the next major version.")]
    public void Deconstruct(
        out Guid id,
        out string contentType,
        out Dictionary<string, object> data,
        out Models.ContentStatus status,
        out Guid createdBy) =>
        (id, contentType, data, status, createdBy) =
            (Id, ContentType, Data, Status, CreatedBy);
}

public record ContentUpdated(
    Guid Id,
    Dictionary<string, object> Data,
    Guid UpdatedBy,
    string? SearchText)
{
    [Obsolete("Use the four-value constructor. Removal planned for the next major version.")]
    public ContentUpdated(
        Guid id,
        Dictionary<string, object> data,
        Guid updatedBy)
        : this(id, data, updatedBy, null)
    {
    }

    [Obsolete("Use the four-value Deconstruct overload. Removal planned for the next major version.")]
    public void Deconstruct(
        out Guid id,
        out Dictionary<string, object> data,
        out Guid updatedBy) =>
        (id, data, updatedBy) =
            (Id, Data, UpdatedBy);
}
public record ContentStatusChanged(Guid Id, Models.ContentStatus NewStatus, Guid UpdatedBy);
