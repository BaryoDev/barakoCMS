using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Rules;

internal static class RuleFieldKinds
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(250);

    public static string? Text(IDictionary<string, object?> data, string? field) => RulePath.Text(data, field, null);

    /// <summary>True when the incoming text of the field differs from the stored text; with no stored entry, when it is not empty.</summary>
    public static bool Changed(RuleCondition c, IDictionary<string, object?> data, IDictionary<string, object?>? stored)
    {
        var now = Shape(data, c.Field);
        return stored is null ? now.Length > 0 : !string.Equals(now, Shape(stored, c.Field), StringComparison.Ordinal);
    }

    /// <summary>
    /// The text a field is compared by. A list or object is its canonical JSON (list order kept, object
    /// keys sorted, numbers as exact decimals), any other value is its text.
    /// </summary>
    private static string Shape(IDictionary<string, object?> data, string? field)
    {
        if (field is null || RulePath.Values(data, field, null) is not { Count: > 0 } found) return "";
        var node = RulePath.Node(found[0]);
        if (node is not (List<object?> or IDictionary<string, object?> or JsonElement { ValueKind: JsonValueKind.Array or JsonValueKind.Object }))
            return RulePath.TextOf(node) ?? "";

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, node);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(Utf8JsonWriter writer, object? value)
    {
        switch (RulePath.Node(value))
        {
            case null:
                writer.WriteNullValue();
                break;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(pair.Key);
                    WriteCanonical(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                WriteCanonical(writer, array.EnumerateArray().Select(e => (object?)e).ToList());
                break;
            case JsonElement { ValueKind: JsonValueKind.Object } obj:
                WriteCanonical(writer, obj.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.Ordinal));
                break;
            case JsonElement { ValueKind: JsonValueKind.Number } number:
                WriteNumber(writer, number.GetRawText());
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case decimal or double or float or int or long or short:
                WriteNumber(writer, Convert.ToString(value, CultureInfo.InvariantCulture)!);
                break;
            case var other:
                JsonSerializer.SerializeToElement(other).WriteTo(writer);
                break;
        }
    }

    /// <summary>A number as an exact decimal, so 1.5 and 1.50 are one value; text that does not fit a decimal is kept as written.</summary>
    private static void WriteNumber(Utf8JsonWriter writer, string raw)
    {
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact))
            writer.WriteRawValue((exact / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture));
        else writer.WriteRawValue(raw);
    }

    public static bool Length(RuleCondition c, IDictionary<string, object?> data) =>
        RuleConditions.Compare(c.Op, (Text(data, c.Field) ?? "").Length.ToString(CultureInfo.InvariantCulture), c.Value);

    public static RuleScan.Held Pattern(RuleCondition c, IDictionary<string, object?> data, TimeSpan timeout)
    {
        var text = Text(data, c.Field) ?? "";
        if (text.Length == 0) return new(true, null);
        try
        {
            return new(new Regex(c.Value ?? "", RegexOptions.CultureInvariant, timeout).IsMatch(text), null);
        }
        catch (RegexMatchTimeoutException)
        {
            return new(false, $"Pattern '{c.Value}' on '{c.Field}' timed out.");
        }
        catch (ArgumentException)
        {
            return new(false, $"Pattern '{c.Value}' on '{c.Field}' is not a valid regex.");
        }
    }

    /// <summary>
    /// The items of an array or object-map field as dictionaries. A plain value becomes
    /// <c>{ "$value": v }</c>, a map entry also carries <c>$key</c>. A missing field is an empty list.
    /// </summary>
    public static (List<IDictionary<string, object?>> Items, string? Error) Items(IDictionary<string, object?> data, string? field)
    {
        var items = new List<IDictionary<string, object?>>();
        if (!data.TryGetValue(field ?? "", out var raw) || raw is null) return (items, null);
        var notArray = (items, $"Field '{field}' is not an array.");
        var node = RulePath.Node(raw);

        if (node is JsonElement { ValueKind: JsonValueKind.Object } map)
            return (map.EnumerateObject().Select(p => MapItem(p.Name, p.Value)).ToList(), null);
        if (node is IDictionary<string, object?> dict)
            return (dict.Select(p => MapItem(p.Key, RulePath.Node(p.Value))).ToList(), null);

        if (node is JsonElement { ValueKind: JsonValueKind.Array } json)
        {
            foreach (var element in json.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    items.Add(PlainItem(element));
                    continue;
                }
                items.Add(element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.OrdinalIgnoreCase));
            }
            return (items, null);
        }

        if (node is not List<object?> list) return notArray;
        foreach (var entry in list.Select(RulePath.Node))
        {
            if (entry is JsonElement { ValueKind: JsonValueKind.Object } element)
                items.Add(element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.OrdinalIgnoreCase));
            else if (entry is IDictionary<string, object?> entryMap)
                items.Add(new Dictionary<string, object?>(entryMap, StringComparer.OrdinalIgnoreCase));
            else items.Add(PlainItem(entry));
        }
        return (items, null);
    }

    private static IDictionary<string, object?> MapItem(string key, object? value)
    {
        var item = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> d => new Dictionary<string, object?>(d, StringComparer.OrdinalIgnoreCase),
            _ => PlainItem(value),
        };
        item["$key"] = key;
        return item;
    }

    private static Dictionary<string, object?> PlainItem(object? value) =>
        new(StringComparer.OrdinalIgnoreCase) { ["$value"] = value is JsonElement e ? FromJson(e) : value };

    private static object? FromJson(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array or JsonValueKind.Object => v,
        _ => null,
    };
}
