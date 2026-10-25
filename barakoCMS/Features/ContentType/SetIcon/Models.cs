using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.SetIcon;

internal class Request
{
    /// <summary>
    /// Either <c>{ "name": "..." }</c>, a sprite id, or <c>{ "svg": "..." }</c>, custom markup that
    /// is sanitised before it is stored. Null clears the icon.
    /// </summary>
    public ContentTypeIcon? Icon { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The icon now stored, SVG as sanitised, or null when the type has none.</summary>
    public ContentTypeIcon? Icon { get; set; }
}
