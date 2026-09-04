using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Blueprints;

/// <summary>A named set of content type definitions, as a blueprint file declares it.</summary>
/// <remarks>
/// The type entries are the same shape <c>POST /api/portability/import</c> takes, so a file can be
/// assembled from an export. A blueprint carries no content, only schema.
/// </remarks>
internal sealed class Blueprint
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ContentTypeDefinition> ContentTypes { get; set; } = new();
}

/// <summary>One blueprint the catalog knows about, valid or not.</summary>
/// <remarks>
/// An invalid file is still an entry, with its problems in <see cref="Errors"/>, so the list shows
/// what is wrong with it. Silently skipping the file would leave the operator looking for a blueprint
/// that never appears and no clue why.
/// </remarks>
internal sealed class BlueprintEntry
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool BuiltIn { get; init; }

    /// <summary>The file name for a custom blueprint, null for a built-in one.</summary>
    public string? Source { get; init; }

    /// <summary>The normalized names of the types applying this blueprint creates.</summary>
    public IReadOnlyList<string> ContentTypes { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool IsValid => Errors.Count == 0;

    /// <summary>The parsed file, or null when it did not parse.</summary>
    public Blueprint? Definition { get; init; }
}
