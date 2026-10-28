using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// A key the caller left out takes its field's <see cref="FieldDefinition.DefaultValue"/>. A key that
/// is sent, whatever its value, is never touched. Every path that stores an entry from outside runs
/// this before validation, so all of them store the same entry.
/// </summary>
public static class FieldDefaults
{
    /// <summary>Fills the absent keys of <paramref name="data"/> in place. Keys match ignoring case.</summary>
    public static void Apply(IEnumerable<FieldDefinition>? fields, IDictionary<string, object> data)
    {
        foreach (var field in fields ?? [])
        {
            if (field.DefaultValue is null)
                continue;

            if (!data.Keys.Any(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase)))
                data[field.Name] = Copy(field.DefaultValue);
        }
    }

    /// <summary>
    /// A value of its own, so an entry never shares an array or object with the definition: a later
    /// edit of one would otherwise change the default of every entry.
    /// </summary>
    private static object Copy(object value) => value switch
    {
        JsonElement element => element.Clone(),
        System.Collections.IDictionary dictionary => CopyDictionary(dictionary),
        string or ValueType => value,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(i => i is null ? null : Copy(i)).ToList(),
        _ => value,
    };

    private static Dictionary<string, object?> CopyDictionary(System.Collections.IDictionary source)
    {
        var copy = new Dictionary<string, object?>();
        foreach (System.Collections.DictionaryEntry entry in source)
            copy[Convert.ToString(entry.Key) ?? string.Empty] = entry.Value is null ? null : Copy(entry.Value);
        return copy;
    }
}
