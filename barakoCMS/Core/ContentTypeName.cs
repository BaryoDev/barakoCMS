namespace barakoCMS.Core;

/// <summary>
/// The stored form of a content type's name.
/// </summary>
/// <remarks>
/// One function because there are two ways in. `POST /api/content-types` slugified the name and the
/// importer stored whatever the file said, while the unique index is on the raw value and every
/// lookup compares with OrdinalIgnoreCase. So an import could create "Article" beside a created
/// "article": two rows the index considers distinct and every reader considers the same one, and
/// which of them a lookup returns is then an accident of ordering.
///
/// Anything that writes the name goes through here.
/// </remarks>
public static class ContentTypeName
{
    public static string Normalize(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "-");
}
