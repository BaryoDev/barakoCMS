namespace barakoCMS.Events;

public record ContentCreated(Guid Id, string ContentType, Dictionary<string, object> Data, Guid CreatedBy);
public record ContentUpdated(Guid Id, Dictionary<string, object> Data, Guid UpdatedBy);
