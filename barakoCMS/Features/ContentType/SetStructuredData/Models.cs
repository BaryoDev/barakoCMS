namespace barakoCMS.Features.ContentType.SetStructuredData;

internal class Request
{
    /// <summary>
    /// The schema.org type a single delivered entry is described as: one of
    /// <c>structuredDataTypes</c> in <c>GET /api/meta/describe</c>, spelled exactly. Null clears it,
    /// and the type emits no structured data.
    /// </summary>
    public string? StructuredDataType { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The type now declared, or null when the type emits none.</summary>
    public string? StructuredDataType { get; set; }
}
