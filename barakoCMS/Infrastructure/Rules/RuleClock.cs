using System.Globalization;
using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>Fills $NOW and $TODAY, with an optional offset such as +2y or -30m, from the rule's clock and zone.</summary>
internal static partial class RuleClock
{
    [GeneratedRegex(@"\$(NOW|TODAY)(?:([+-])(\d{1,4})(mo|y|d|h|m))?", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public static string Fill(string template, RuleValues values) =>
        template.Contains('$') ? Token().Replace(template, m => Render(m, values)) : template;

    private static string Render(Match m, RuleValues values)
    {
        var local = TimeZoneInfo.ConvertTime(values.Now, values.Zone);
        if (m.Groups[2].Success)
        {
            var n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value == "-" ? -1 : 1);
            local = m.Groups[4].Value switch
            {
                "y" => local.AddYears(n),
                "mo" => local.AddMonths(n),
                "d" => local.AddDays(n),
                "h" => local.AddHours(n),
                _ => local.AddMinutes(n),
            };
        }

        return m.Groups[1].Value == "NOW"
            ? local.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>True when any text in the rule names the clock, so the site zone is read only for such rules.</summary>
    public static bool Used(barakoCMS.Models.RuleDefinition rule)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(rule);
        return text.Contains("$NOW", StringComparison.Ordinal) || text.Contains("$TODAY", StringComparison.Ordinal);
    }
}
