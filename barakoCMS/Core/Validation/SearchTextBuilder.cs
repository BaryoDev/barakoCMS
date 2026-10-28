using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace barakoCMS.Core.Validation;

/// <summary>
/// Builds the derived SearchText from an entry's data. Every write path uses this, so a list or an
/// object lands as its words and never as a CLR type name or raw JSON.
/// </summary>
public static class SearchTextBuilder
{
    /// <summary>The scalar words of the fields named in <paramref name="searchable"/>, space separated.</summary>
    public static string Build(IEnumerable<KeyValuePair<string, object>> data, ISet<string> searchable) =>
        string.Join(
            ' ',
            data.Where(kv => searchable.Contains(kv.Key))
                .Select(kv => Flatten(kv.Value))
                .Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>
    /// Strings as they are, numbers and booleans as invariant text, arrays as their items and objects
    /// as their values, joined by spaces, recursively. Nulls contribute nothing.
    /// </summary>
    public static string Flatten(object? value)
    {
        var words = new List<string>();
        Collect(value, words);
        return string.Join(' ', words);
    }

    private static void Collect(object? value, List<string> words)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                Add(text, words);
                return;
            case JsonElement element:
                CollectElement(element, words);
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary) Collect(entry.Value, words);
                return;
            case IEnumerable items:
                foreach (var item in items) Collect(item, words);
                return;
            case bool flag:
                words.Add(flag ? "true" : "false");
                return;
            case DateTime moment:
                words.Add(moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                return;
            case DateTimeOffset moment:
                words.Add(moment.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                return;
            case IFormattable formattable:
                words.Add(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            default:
                Add(value.ToString(), words);
                return;
        }
    }

    private static void CollectElement(JsonElement element, List<string> words)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                Add(element.GetString(), words);
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                words.Add(element.GetRawText());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectElement(item, words);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectElement(property.Value, words);
                break;
        }
    }

    private static void Add(string? text, List<string> words)
    {
        if (!string.IsNullOrWhiteSpace(text)) words.Add(text);
    }
}
