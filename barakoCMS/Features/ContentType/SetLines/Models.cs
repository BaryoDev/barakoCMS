using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetLines;

internal class Request
{
    /// <summary>The lines the type takes from now on, or null to take none.</summary>
    public LinesDefinition? Lines { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    public LinesDefinition? Lines { get; set; }
}
