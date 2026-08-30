namespace barakoCMS.Features.Content.History;

internal class Request : barakoCMS.Models.ListRequest
{
    public Guid Id { get; set; }
}

internal class VersionResponse
{
    public Guid Id { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
    public Guid LastModifiedBy { get; set; }
    public Guid VersionId { get; set; }

    // Always UTC. Marten hands back the event's timestamp in the server's local offset, and the
    // same instant written two ways in one API is how a client ends up with two date parsers.
    public DateTimeOffset Timestamp { get; set; }
}

// The history used to come back as {versions: [...]}. It is a collection like any other and now
// uses the same envelope, so a client can page a long-lived document's history.

