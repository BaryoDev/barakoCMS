namespace BarakoCMS.Accounting;

/// <summary>
/// Case-insensitive access to a content entry's untyped <c>Data</c> bag.
///
/// Field names are declared PascalCase on a content type, but a JSON client following the usual web
/// convention sends camelCase, and the bag stores whatever keys actually arrived. barakoCMS's schema
/// validator already resolves this by matching field names case-insensitively
/// (<c>ContentValidatorService</c>); these helpers apply the same rule so a hook agrees with the
/// validator about which key it is looking at.
/// </summary>
internal static class ContentData
{
    public static object? Get(IReadOnlyDictionary<string, object> data, string field)
    {
        if (data.TryGetValue(field, out var exact)) return exact;

        foreach (var (key, value) in data)
            if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase))
                return value;

        return null;
    }

    public static bool Has(IReadOnlyDictionary<string, object> data, string field) =>
        data.Keys.Any(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes a server-owned value under the field's canonical name, dropping any differently-cased
    /// key first so the entry never ends up carrying both <c>amount</c> and <c>Amount</c>.
    /// </summary>
    public static void Set(Dictionary<string, object> data, string field, object value)
    {
        foreach (var key in data.Keys
                     .Where(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase) && k != field)
                     .ToList())
        {
            data.Remove(key);
        }

        data[field] = value;
    }

    public static string? AsString(object? v) => v?.ToString();

    public static decimal AsDecimal(object? v) => v switch
    {
        null => 0m,
        decimal d => d,
        long l => l,
        int i => i,
        // Should not occur now money round-trips as decimal (see ObjectJsonConverter), but convert
        // rather than throw so a legacy or hand-written payload still gets a real validation message.
        double dbl => (decimal)dbl,
        string s when decimal.TryParse(s, out var parsed) => parsed,
        _ => 0m,
    };
}
