namespace barakoCMS.Features.Content.History;

public class Request
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

public class Response
{
    public List<VersionResponse> Versions { get; set; } = new();
}
