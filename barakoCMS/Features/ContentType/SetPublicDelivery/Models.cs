namespace barakoCMS.Features.ContentType.SetPublicDelivery;

public class Request
{
    /// <summary>True to serve this type from the anonymous public delivery API.</summary>
    public bool Enabled { get; set; }
}

public class Response
{
    public string Name { get; set; } = string.Empty;
    public bool IsPubliclyDeliverable { get; set; }
}
