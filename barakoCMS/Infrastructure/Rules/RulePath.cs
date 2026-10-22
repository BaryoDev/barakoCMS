using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Reads values by path: A.B, A[last], A[0], A[Field=value], A.$keys, parent.X inside each, let.x.</summary>
internal static class RulePath
{
    public static List<object?> Values(IDictionary<string, object?> data, string path, RuleValues? values)
    {
        var segments = Split(path);
        IEnumerable<object?> nodes = [data];
        if (segments.Count > 1 && values is not null && segments[0].Equals("let", StringComparison.OrdinalIgnoreCase))
        {
            nodes = [values.Lets];
            segments.RemoveAt(0);
        }
        else if (segments.Count > 1 && values?.Parent is { } parent && segments[0].Equals("parent", StringComparison.OrdinalIgnoreCase))
        {
            nodes = [parent];
            segments.RemoveAt(0);
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, last) = (segments[i], i == segments.Count - 1);
            nodes = nodes.SelectMany(n => Step(n, segment, last)).ToList();
        }

        return nodes.ToList();
    }

    /// <summary>The values with any list among them replaced by its items.</summary>
    public static List<object?> Spread(List<object?> values) =>
        values.SelectMany(v => Items(Node(v)) ?? [Node(v)]).ToList();

    public static string? Text(IDictionary<string, object?> data, string? path, RuleValues? values) =>
        path is not null && Values(data, path, values) is { Count: > 0 } found ? TextOf(found[0]) : null;

    /// <summary>JSON text becomes a JsonElement, a list becomes a list, a typed object becomes its JSON shape.</summary>
    public static object? Node(object? value) => value switch
    {
        null or JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement { ValueKind: JsonValueKind.String } s when s.GetString()!.TrimStart() is ['[' or '{', ..] => Parse(s.GetString()!) ?? value,
        JsonElement or IDictionary<string, object?> => value,
        string s when s.TrimStart() is ['[' or '{', ..] => Parse(s) ?? value,
        string or bool or decimal or double or float or int or long or short or Guid or DateTime or DateTimeOffset => value,
        IEnumerable list => list.Cast<object?>().ToList(),
        _ => JsonSerializer.SerializeToElement(value),
    };

    public static string? TextOf(object? value) => Node(value) switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } s => s.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement j => j.GetRawText(),
        bool b => b ? "true" : "false",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("O", CultureInfo.InvariantCulture),
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    private static IEnumerable<object?> Step(object? node, string segment, bool last)
    {
        if (segment == "$keys") return Keys(Node(node));
        var open = segment.IndexOf('[');
        var value = Node(Child(Node(node), open < 0 ? segment : segment[..open]));
        if (open < 0) return !last && Items(value) is { } spread ? spread : [value];
        return Pick(Items(value) ?? [], segment[(open + 1)..^1].Trim());
    }

    private static IEnumerable<object?> Pick(List<object?> items, string pick)
    {
        if (pick.Equals("last", StringComparison.OrdinalIgnoreCase)) return items.Count == 0 ? [] : [items[^1]];
        if (int.TryParse(pick, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return index < items.Count ? [items[index]] : [];
        var eq = pick.IndexOf('=');
        if (eq < 0) return [];
        var (field, want) = (pick[..eq].Trim(), pick[(eq + 1)..].Trim());
        return items.Where(item => string.Equals(TextOf(Child(Node(item), field)), want, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static object? Child(object? node, string name)
    {
        switch (node)
        {
            case JsonElement { ValueKind: JsonValueKind.Object } json:
                foreach (var p in json.EnumerateObject())
                {
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
                }
                return null;
            case IDictionary<string, object?> map:
                return map.TryGetValue(name, out var v) ? v : map.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            default:
                return null;
        }
    }

    private static List<object?>? Items(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Array } json => json.EnumerateArray().Select(e => (object?)e).ToList(),
        List<object?> list => list,
        _ => null,
    };

    private static List<object?> Keys(object? node) => node switch
    {
        JsonElement { ValueKind: JsonValueKind.Object } json => json.EnumerateObject().Select(p => (object?)p.Name).ToList(),
        IDictionary<string, object?> map => map.Keys.Cast<object?>().ToList(),
        _ => [],
    };

    private static object? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> Split(string path)
    {
        var parts = new List<string>();
        var (depth, start) = (0, 0);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[') depth++;
            else if (path[i] == ']') depth--;
            else if (path[i] == '.' && depth == 0)
            {
                parts.Add(path[start..i]);
                start = i + 1;
            }
        }

        parts.Add(path[start..]);
        return parts;
    }
}
