using barakoCMS.Core.Validation;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Content.List;

/// <summary>
/// Keeps <c>token</c> fields out of the entries list <c>search</c>.
/// </summary>
/// <remarks>
/// The search matches a substring of any value in an entry, including values the caller is not
/// shown. For most fields that tells a caller an entry holds the text somewhere. For a token it
/// would give the value away a character at a time: search for one more character, and see whether
/// the entry is still in the list. So a token's value is not matched for anyone, including a
/// caller who may read it. Such a caller finds an entry by its token with
/// <c>filter[Field][eq]=</c>, which is refused for a caller who may not read the field.
/// </remarks>
internal static class TokenSearch
{
    /// <summary>
    /// The search predicate, leaving out the value under each <c>type/field</c> pair it is given.
    /// </summary>
    /// <remarks>
    /// Parameters, in order: the escaped term, then the pairs as a text array, each in lower case.
    /// A field name is letters and digits, so the last slash of a pair is the one that splits it.
    /// </remarks>
    public const string Sql =
        "EXISTS (SELECT 1 FROM jsonb_each_text(d.data -> 'Data') kv WHERE kv.value ILIKE '%' || ? || '%' "
        + "AND NOT ((lower(coalesce(d.data ->> 'ContentType', '')) || '/' || lower(kv.key)) = ANY(?)))";

    /// <summary>Every token field of the tenant's content types, as lower case <c>type/field</c>.</summary>
    public static async Task<string[]> KeysAsync(IQuerySession session, CancellationToken ct)
    {
        var definitions = await session.Query<ContentTypeDefinition>().ToListAsync(ct);

        return definitions
            .SelectMany(definition => (definition.Fields ?? [])
                .Where(field => field is not null && TokenFields.IsToken(field.Type))
                .Select(field => $"{definition.Name.ToLowerInvariant()}/{field.Name.ToLowerInvariant()}"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
