using System.Text.Json;
using barakoCMS.Infrastructure.Serialization;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Reads the text a workflow action is given as the list or object a json field holds.
/// </summary>
/// <remarks>
/// A parameter is text, and a template can only fill it with text, so a json, object or array field
/// reaches an action as JSON text. Stored as that text it would be a string in a field that holds a
/// list or an object. Every other field keeps the text it was given.
/// </remarks>
internal static class JsonTextFields
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new ObjectJsonConverter() },
    };

    /// <summary>Whether <paramref name="field"/> holds a list or an object, so text for it is JSON.</summary>
    public static bool Holds(FieldDefinition? field) =>
        field?.Type is { } type
        && (string.Equals(type, "json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "object", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "array", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The list (<c>List&lt;object&gt;</c>) or object (<c>Dictionary&lt;string, object&gt;</c>) that
    /// <paramref name="text"/> holds, or false when it is not a JSON array or object.
    /// </summary>
    public static bool TryRead(string text, out object value)
    {
        value = null!;
        try
        {
            // The text alone, with default options, so trailing content or a second value is an error.
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            {
                return false;
            }

            // Wrapped only now, and from the text the parser accepted as one value, so it cannot
            // add a key or close the wrapper early. The wrapper lets an array read through the
            // same converter as an object.
            var wrapped = JsonSerializer.Deserialize<Dictionary<string, object>>(
                $"{{\"v\":{document.RootElement.GetRawText()}}}", Options);
            if (wrapped is null || !wrapped.TryGetValue("v", out var read) || read is null)
            {
                return false;
            }

            value = read;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces, in <paramref name="data"/>, the text given for each field in <paramref name="fields"/>
    /// that holds a list or an object with the list or object it holds. Returns the refusal message
    /// for the first such field whose text is not a JSON array or object, or whose
    /// <paramref name="isStructured"/> says it was not one whole placeholder for a list or an object,
    /// or null when none is.
    /// </summary>
    public static string? Read(
        IEnumerable<FieldDefinition?>? fields, Dictionary<string, object> data, Func<string, bool> isStructured)
    {
        foreach (var key in data.Keys.ToList())
        {
            var field = fields?.FirstOrDefault(f => f is not null && string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (!Holds(field))
            {
                continue;
            }

            if (!isStructured(key) || data[key] is not string text || !TryRead(text, out var parsed))
            {
                return Refusal(field!.Name);
            }

            data[key] = parsed;
        }

        return null;
    }

    /// <summary>The refusal for a json field given anything but one whole list or object. It does not name the value.</summary>
    public static string Refusal(string field) =>
        $"Field '{field}' holds a list or an object, and the value this action was given is not a whole list or object from one placeholder.";
}
