using Marten;

using barakoCMS.Models;

// In the global namespace so every file that looks a definition up can call it unqualified.

/// <summary>Looks a content type definition up by name.</summary>
public static class ContentTypeDefinitionQueries
{
    /// <summary>
    /// The definition <paramref name="name"/> stands for, ignoring case, chosen the same way every
    /// time. See <see cref="barakoCMS.Core.ContentTypeName.Pick"/>.
    /// </summary>
    public static async Task<ContentTypeDefinition?> FirstNamedAsync(
        this IQueryable<ContentTypeDefinition> definitions, string? name, CancellationToken ct = default) =>
        barakoCMS.Core.ContentTypeName.Pick(
            await definitions.Where(barakoCMS.Core.ContentTypeName.Named(name)).ToListAsync(ct), name);
}
