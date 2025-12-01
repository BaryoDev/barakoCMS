namespace barakoCMS.Events;

public record ContentCreated(Guid Id, string ContentType, Dictionary<string, object> Data, Models.ContentStatus Status, Guid CreatedBy);
public record ContentUpdated(Guid Id, Dictionary<string, object> Data, Guid UpdatedBy);
public record ContentStatusChanged(Guid Id, Models.ContentStatus NewStatus, Guid UpdatedBy);
