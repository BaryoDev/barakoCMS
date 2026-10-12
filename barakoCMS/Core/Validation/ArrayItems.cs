using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What an <c>array</c> field that declares an <see cref="FieldDefinition.ItemType"/> accepts: a
/// list whose every item is a value of that type.
/// </summary>
/// <remarks>
/// An array with no item type accepts any list, as it always has, and nothing here applies to it.
///
/// Only types whose check is a function of the value alone may be an item type. A reference needs
/// the database and is declared as a <c>reference</c> field with <c>multiple</c>, which checks
/// every id and which <c>include</c> and the <c>has</c> filter read. A choice needs its options,
/// and is a <c>choice</c> field with <c>multiple</c>.
/// </remarks>
internal static class ArrayItems
{
    /// <summary>The types an array's items may be declared as, in the order an error lists them.</summary>
    public static IReadOnlyList<string> ItemTypes { get; } =
    [
        "string", "text", "markdown", "int", "decimal", "bool", "date", "datetime", "time",
        "email", "url", "slug", "uuid",
    ];

    public static bool IsArray(string? type) =>
        string.Equals(type, "array", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is wrong with the item type a field declares, for the type validator.</summary>
    public static IEnumerable<string> DefinitionErrors(FieldDefinition field)
    {
        if (field.ItemType is null)
            yield break;

        if (!IsArray(field.Type))
        {
            yield return $"Field '{field.Name}' declares itemType but is of type '{field.Type}', not array.";
            yield break;
        }

        if (string.Equals(field.ItemType, "reference", StringComparison.OrdinalIgnoreCase))
            yield return $"Field '{field.Name}' declares itemType 'reference'. A list of references is a "
                       + "reference field with multiple set, which checks each id.";
        else if (!ItemTypes.Contains(field.ItemType, StringComparer.Ordinal))
            yield return $"Field '{field.Name}' declares itemType '{field.ItemType}'. An array's items may be "
                       + $"{string.Join(", ", ItemTypes)}.";
    }

    /// <summary>What is wrong with a value written to an array field, or null.</summary>
    /// <remarks>
    /// The list the entry already holds is not checked again, as a choice's is not: an item type
    /// declared after the entry was written would otherwise refuse every edit of the rest of it.
    /// </remarks>
    public static string? ValueError(FieldDefinition field, object value, Content? existing)
    {
        if (field.ItemType is not { } itemType || !IsArray(field.Type))
            return null;

        if (StoredValues.IsUnchanged(field.Name, value, existing))
            return null;

        var index = 0;
        foreach (var item in Items(value))
        {
            if (item is null || !FieldTypeRegistry.IsValidValue(itemType, item))
                return $"Field '{field.DisplayName}' ({field.Name}) holds items of type '{itemType}', "
                     + $"and the item at index {index} is not one.";
            index++;
        }

        return null;
    }

    private static IEnumerable<object?> Items(object value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Array } je)
        {
            foreach (var item in je.EnumerateArray())
                yield return item.ValueKind == JsonValueKind.Null ? null : item;
            yield break;
        }

        if (value is System.Collections.IEnumerable list and not string)
        {
            foreach (var item in list)
                yield return item is JsonElement { ValueKind: JsonValueKind.Null } ? null : item;
        }
    }
}
