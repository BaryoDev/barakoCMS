using System.Globalization;
using System.Text.Json;

namespace BarakoCMS.Pages;

/// <summary>Reads tree fields out of an entry's data, whatever shape the value was stored in.</summary>
/// <remarks>
/// A value arrives as a <see cref="JsonElement"/> when read back from the database and as a CLR value
/// when it came in on a request, so each reader accepts both. Field names match case-insensitively,
/// the same as schema validation.
/// </remarks>
internal static class PageData
{
    public static object? Raw(IReadOnlyDictionary<string, object> data, string field) =>
        data.FirstOrDefault(kv => string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase)).Value;

    public static string? String(IReadOnlyDictionary<string, object> data, string field) =>
        Raw(data, field) switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement => null,
            var v => v.ToString(),
        };

    public static Guid? Guid(IReadOnlyDictionary<string, object> data, string field) =>
        System.Guid.TryParse(String(data, field), out var id) ? id : null;

    public static bool Bool(IReadOnlyDictionary<string, object> data, string field) =>
        Raw(data, field) switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } je => bool.TryParse(je.GetString(), out var s) && s,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };

    public static int? Int(IReadOnlyDictionary<string, object> data, string field) =>
        Raw(data, field) switch
        {
            null => null,
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt32(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } je
                when int.TryParse(je.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
}
