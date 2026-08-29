namespace barakoCMS.Features.Content.History;

public class Request : barakoCMS.Models.ListRequest
{
    public Guid Id { get; set; }
}

public class VersionResponse
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
    public Guid LastModifiedBy { get; set; }
    public Guid VersionId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

// The history used to come back as {versions: [...]}. It is a collection like any other and now
// uses the same envelope, so a client can page a long-lived document's history.

