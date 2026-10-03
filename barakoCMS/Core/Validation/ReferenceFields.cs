using Marten;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a <c>reference</c> field with <see cref="FieldDefinition.Multiple"/> set holds: a list of
/// entry ids, each checked on write to name an entry of the declared type in this tenant.
/// </summary>
/// <remarks>
/// A reference without <c>multiple</c> holds one id, as it always has, and nothing here applies to
/// it. No endpoint changes a stored field's type or <c>multiple</c>, so a stored single reference
/// stays one.
///
/// Each id is held in the form this API writes ids in, lower case with hyphens, so a delivery
/// filter can match an element exactly without asking the database to fold case.
/// </remarks>
internal static class ReferenceFields
{
    /// <summary>The most ids one many-valued reference holds.</summary>
    /// <remarks>
    /// An include resolves every id on a page, so this bounds what one page can ask for together
    /// with the page size and <c>PublicDelivery.MaxIncludedEntries</c>.
    /// </remarks>
    public const int MaxIds = 100;

    /// <summary>How many ids one error names before it says how many more there are.</summary>
    private const int NamedInError = 5;

    public static bool IsMultiple(FieldDefinition field) =>
        field.Multiple && string.Equals(field.Type, "reference", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ids a value holds, in order, or false when it is not a list of text.</summary>
    public static bool TryReadList(object? value, out List<string> ids) =>
        FieldTypeRegistry.TryReadChoice(value, out ids, out var isList) && isList;

    /// <summary>Whether text is an id written the way <see cref="Guid.ToString()"/> writes one.</summary>
    public static bool IsCanonicalId(string text) =>
        text.Length == 36 && Guid.TryParseExact(text, "D", out var id) && id.ToString() == text;

    /// <summary>What is wrong with a value written to a many-valued reference, or null.</summary>
    /// <remarks>
    /// One query for every id. The session is the writer's, so an entry in another tenant is not
    /// found and reads as one that does not exist.
    /// </remarks>
    public static async Task<string?> ValueErrorAsync(IQuerySession session, FieldDefinition field, object value)
    {
        if (!TryReadList(value, out var texts))
            return $"Field '{field.DisplayName}' holds a list of references, so send a list of ids, even of one.";

        if (texts.Count > MaxIds)
            return $"Field '{field.DisplayName}' holds at most {MaxIds} references and received {texts.Count}.";

        if (!texts.All(IsCanonicalId))
            return $"Field '{field.DisplayName}' expects each reference as an id in lower case with hyphens, "
                 + "the form this API returns ids in.";

        var ids = texts.Select(t => Guid.Parse(t)).ToList();

        if (ids.Distinct().Count() != ids.Count)
            return $"Field '{field.DisplayName}' lists the same reference more than once.";

        if (ids.Count == 0)
            return null;

        var asked = ids.ToArray();
        var found = (await session.Query<ContentDoc>()
                .Where(c => c.Id.In(asked))
                .ToListAsync())
            .ToDictionary(c => c.Id);

        var missing = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            return $"Field '{field.DisplayName}' references {Named(missing)}, which "
                 + (missing.Count == 1 ? "does" : "do") + " not exist.";

        // The type each wrong entry is of is not named, unlike the single reference's error: a
        // writer is not always allowed to read the entries they name.
        var wrong = ids
            .Where(id => !string.Equals(found[id].ContentType, field.ReferenceType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (wrong.Count > 0)
            return $"Field '{field.DisplayName}' points at '{field.ReferenceType}', and {Named(wrong)} "
                 + (wrong.Count == 1 ? "is" : "are") + " not one.";

        return null;
    }

    private static string Named(List<Guid> ids) =>
        ids.Count <= NamedInError
            ? string.Join(", ", ids)
            : $"{string.Join(", ", ids.Take(NamedInError))} and {ids.Count - NamedInError} more";
}
