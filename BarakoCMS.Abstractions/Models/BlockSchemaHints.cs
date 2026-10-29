namespace barakoCMS.Models;

/// <summary>
/// The optional keys a renderer may add to a block's stored schema so an editor gets the label, kind
/// and per-viewer flag the renderer meant, instead of defaults read off the JSON Schema.
/// </summary>
/// <remarks>
/// <see cref="Label"/> and <see cref="PerViewer"/> go on a block's schema, <see cref="Label"/> and
/// <see cref="Kind"/> on one of its properties. They are read only by <c>GET /api/pages/block-schema</c>
/// and never affect whether a block is valid.
/// </remarks>
public static class BlockSchemaHints
{
    /// <summary>What an editor calls the block or the field. Text, 1 to 100 characters.</summary>
    public const string Label = "x-label";

    /// <summary>The field kind, one of <see cref="Kinds"/>.</summary>
    public const string Kind = "x-kind";

    /// <summary>True when the block shows something that depends on who is looking.</summary>
    public const string PerViewer = "x-perViewer";

    public const int MaxLabelLength = 100;

    public static IReadOnlyList<string> Kinds { get; } =
        ["text", "markdown", "url", "number", "boolean", "select", "slots"];
}
