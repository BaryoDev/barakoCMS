using barakoCMS.Core;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.AI.Features;

/// <summary>Finds the content type a route names, the way core and the other modules do.</summary>
internal static class TypeLookup
{
    /// <summary>
    /// The definition for <paramref name="type"/>, ignoring case, or null. The stored name is the one to
    /// query and store with, since content and embeddings carry it as written.
    /// </summary>
    /// <exception cref="AmbiguousContentTypeException">Two stored names differ only by case and none is exact.</exception>
    public static async Task<ContentTypeDefinition?> FindAsync(IQuerySession session, string type, CancellationToken ct) =>
        ContentTypeName.Pick(
            await session.Query<ContentTypeDefinition>().Where(ContentTypeName.Named(type)).ToListAsync(ct), type);
}
