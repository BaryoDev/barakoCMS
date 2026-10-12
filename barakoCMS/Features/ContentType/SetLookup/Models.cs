using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetLookup;

internal class Request
{
    /// <summary>The lookup the type answers from now on, or null to answer none.</summary>
    public LookupDefinition? Lookup { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    public LookupDefinition? Lookup { get; set; }
}
