using System.Globalization;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Fills rule templates: <c>{{field}}</c>, <c>{{= expr }}</c>, <c>{{lookup.name.number}}</c> and <c>$CURRENT_USER</c>.</summary>
internal static partial class RuleTemplates
{
    private const string LookupPrefix = "lookup.";

    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// The filled text. A missing field fills empty. A lookup not resolved fills empty and sets
    /// <paramref name="missing"/> to the reference.
    /// </summary>
    public static string Fill(
        string template, IDictionary<string, object?> data, Guid userId,
        IReadOnlyDictionary<string, RuleLookupValue> resolved, out string? missing, RuleValues? values = null)
    {
        string? firstMissing = null;
        if (values is not null) template = RuleClock.Fill(template, values);
        var filled = Placeholder().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            if (name.StartsWith('='))
                return RuleCalc.Evaluate(name[1..].Trim(), path => RulePath.Values(data, path, values));
            if (!name.StartsWith(LookupPrefix, StringComparison.OrdinalIgnoreCase))
                return RulePath.Text(data, name, values) ?? "";

            var text = LookupText(name, resolved);
            if (text is null) firstMissing ??= name;
            return text ?? "";
        });

        missing = firstMissing;
        return filled.Replace("$CURRENT_USER", userId.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The text of <c>lookup.&lt;name&gt;.&lt;part&gt;</c>, or null when the lookup is not resolved.</summary>
    private static string? LookupText(string reference, IReadOnlyDictionary<string, RuleLookupValue> resolved)
    {
        var rest = reference[LookupPrefix.Length..];
        var dot = rest.LastIndexOf('.');
        if (dot <= 0) return null;

        if (!resolved.TryGetValue(rest[..dot], out var value)) return null;
        return rest[(dot + 1)..].ToLowerInvariant() switch
        {
            "number" => value.Number?.ToString(CultureInfo.InvariantCulture) ?? "",
            "text" => value.Text ?? "",
            "found" => value.Found ? "true" : "false",
            _ => null,
        };
    }
}
