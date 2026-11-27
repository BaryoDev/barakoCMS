using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>
/// The values a saved query's parameters take when it runs for an entry: <c>@id</c>,
/// <c>@createdAt</c>, <c>@data.Field</c> and <c>@actions.N.name</c>. One binding for the Query step
/// and for a template's datasets, so a parameter means the same in both.
/// </summary>
internal static class QueryBinding
{
    /// <summary>The value of each parameter the query's filters name. A parameter with no value is left out, and the run then refuses.</summary>
    public static (Dictionary<string, string> Values, string? Problem) Bind(
        QueryDefinition definition, Content content, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? outputs)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in definition.Filters.Select(f => f.Param).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
        {
            var value = Value(name!, content, outputs);
            if (value is not null) values[name!] = value;
        }

        return (values, null);
    }

    private static string? Value(string name, Content content, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? outputs)
    {
        if (name == "id") return content.Id.ToString();
        if (name == "createdAt") return content.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var parts = name.Split('.');
        if (parts is ["data", var field])
        {
            if (content.Data is null) return null;
            foreach (var (key, value) in content.Data)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) return value is null ? null : Text(value);
            }

            return null;
        }

        if (parts is ["actions", var step, var output] && int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
        {
            return outputs is not null && outputs.TryGetValue(ordinal, out var kept) && kept.TryGetValue(output, out var text) ? text : null;
        }

        return null;
    }

    /// <summary>A stored value as the text an output holds. Numbers and dates are written the same way for every reader.</summary>
    internal static string Text(object value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}
