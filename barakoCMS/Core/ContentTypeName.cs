using System.Linq.Expressions;
using barakoCMS.Models;

namespace barakoCMS.Core;

/// <summary>
/// The stored form of a content type's name, and how a name given by a caller is matched to one.
/// </summary>
/// <remarks>
/// Two ways in. `POST /api/content-types` and config apply store the name through
/// <see cref="Normalize"/> (lowercase), while a module that seeds a type keeps the case it gave
/// ("PurchaseOrder"). A client, a document or a workflow names the type in whatever case it likes,
/// so a name is matched to a stored type ignoring case, never by exact string.
///
/// Anything that writes the name goes through <see cref="Normalize"/>. Anything that looks a type
/// up by name goes through <see cref="Named(string?)"/> or <see cref="Same"/>.
/// </remarks>
public static class ContentTypeName
{
    public static string Normalize(string? name) =>
        (name ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "-");

    /// <summary>Whether two names are the same type, ignoring case.</summary>
    /// <remarks>Null and blank match nothing, not even each other.</remarks>
    public static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The definitions named <paramref name="name"/>, ignoring case, as a query filter.</summary>
    public static Expression<Func<ContentTypeDefinition, bool>> Named(string? name)
    {
        var lowered = (name ?? string.Empty).ToLower();
        return d => d.Name.ToLower() == lowered;
    }

    /// <summary>
    /// The one definition a name stands for: the exact spelling when one is stored, else the only
    /// one that differs in case, else null.
    /// </summary>
    /// <exception cref="AmbiguousContentTypeException">
    /// Two or more stored names differ from <paramref name="wanted"/> only by case and none is
    /// exact. Legacy data can hold both a module-seeded "PurchaseOrder" and a config
    /// "purchaseorder"; picking either would be a guess, so the lookup refuses.
    /// </exception>
    public static ContentTypeDefinition? Pick(IReadOnlyList<ContentTypeDefinition> matches, string? wanted)
    {
        foreach (var match in matches)
        {
            if (string.Equals(match.Name, wanted, StringComparison.Ordinal)) return match;
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new AmbiguousContentTypeException(wanted ?? string.Empty),
        };
    }

    /// <summary>
    /// The item whose name is <paramref name="wanted"/>: the exact spelling when one is held, else
    /// the only one that differs in case, else null (also null when several differ only in case).
    /// </summary>
    /// <remarks>
    /// Exact first, so a caller that spelled the name as it is stored gets the same item it always
    /// got. Matching ignores case only; it never matches a different name, and a blank wanted name
    /// matches only a blank held name, as an exact comparison did.
    /// </remarks>
    public static T? PickByName<T>(IEnumerable<T> items, Func<T, string?> nameOf, string? wanted) where T : class
    {
        T? loose = null;
        var looseCount = 0;
        foreach (var item in items)
        {
            var name = nameOf(item);
            if (string.Equals(name, wanted, StringComparison.Ordinal)) return item;
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                loose ??= item;
                looseCount++;
            }
        }

        // Two spellings, neither exact: which one is meant is a guess, and a guess at a permission
        // is a way round it. Nothing matches, so nothing is granted.
        return looseCount == 1 ? loose : null;
    }
}

/// <summary>Thrown when a name matches more than one stored type and none spells it exactly.</summary>
public sealed class AmbiguousContentTypeException(string name) : Exception(
    $"Content type '{name}' matches more than one stored type that differ only by case. Name it exactly as stored.");
