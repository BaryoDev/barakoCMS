namespace barakoCMS.Events;

public record ContentCreated(Guid Id, string ContentType, Dictionary<string, object> Data, Models.ContentStatus Status, Guid CreatedBy, string SearchText);
public record ContentUpdated(Guid Id, Dictionary<string, object> Data, Guid UpdatedBy, string SearchText);
public record ContentStatusChanged(Guid Id, Models.ContentStatus NewStatus, Guid UpdatedBy);
