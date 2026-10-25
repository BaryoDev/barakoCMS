namespace barakoCMS.Models;

/// <summary>
/// The icon a content type is drawn with: a glyph from the client's sprite, named, or a custom
/// SVG, never both.
/// </summary>
public class ContentTypeIcon
{
    /// <summary>A sprite id: letters, digits and hyphens. The server does not know the sprite.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Custom markup, stored already sanitised: a <c>viewBox</c>, allowlisted shapes, every shape
    /// filled with <c>currentColor</c>.
    /// </summary>
    public string? Svg { get; set; }
}
