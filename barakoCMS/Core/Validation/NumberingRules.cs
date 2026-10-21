using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>A content type's <see cref="NumberingRule"/>: what it may say, and the series it counts in.</summary>
internal static class NumberingRules
{
    public const int MaxPrefixLength = 20;

    private static readonly string[] TextTypes = ["string", "text"];

    /// <summary>The series a type's entries are numbered in: the type's own, so a rename of the field keeps counting.</summary>
    public static NumberSeries SeriesFor(ContentTypeDefinition definition, NumberingRule rule) =>
        SeriesFor(definition, rule, null);

    /// <summary>
    /// The series for a rule, with the rule's template filled in. Without a template it is the type's
    /// own series. A template with <c>{branch}</c> needs the code of the branch the write is for.
    /// </summary>
    /// <exception cref="InvalidOperationException">The template needs a branch and none was given.</exception>
    public static NumberSeries SeriesFor(ContentTypeDefinition definition, NumberingRule rule, string? branchCode)
    {
        var name = rule.SeriesTemplate is { Length: > 0 } template
            ? ExpandTemplate(template, definition.Name, branchCode)
            : "type." + definition.Name.ToLowerInvariant();
        return new NumberSeries(name.Length > 64 ? name[..64] : name, rule.Prefix, rule.Padding, rule.Reset, rule.TimeZoneId);
    }

    /// <summary>Whether a rule's series name depends on the branch.</summary>
    public static bool NeedsBranch(NumberingRule rule) =>
        rule.SeriesTemplate?.Contains("{branch}", StringComparison.Ordinal) == true;

    /// <summary>
    /// The names of the type's client-allocated series, with the branch left as a pattern, so a
    /// ledger or a check can tell which counters a client may take numbers from.
    /// </summary>
    public static IReadOnlyList<string> ClientSeriesNames(ContentTypeDefinition definition) =>
        definition.Numbering is { ClientAllocated: true } rule
            ? [SeriesFor(definition, rule, NeedsBranch(rule) ? "{branch}" : null).Name]
            : [];

    private static string ExpandTemplate(string template, string typeName, string? branchCode)
    {
        if (template.Contains("{branch}", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(branchCode))
                throw new InvalidOperationException("The series template needs a branch and the request has none.");
            template = template.Replace("{branch}", branchCode.ToLowerInvariant(), StringComparison.Ordinal);
        }

        return template.Replace("{type}", typeName.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>What is wrong with a rule for this type.</summary>
    public static List<string> DefinitionErrors(ContentTypeDefinition definition, NumberingRule rule)
    {
        var errors = new List<string>();
        var field = definition.Fields.FirstOrDefault(f => string.Equals(f.Name, rule.Field, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            errors.Add($"'{definition.Name}' has no field '{rule.Field}' to hold the number.");
        }
        else if (!TextTypes.Contains(field.Type.ToLowerInvariant()))
        {
            errors.Add($"'{field.Name}' is a {field.Type}; a number with its prefix is text, so the field has to be string or text.");
        }
        else if (field.IsRequired)
        {
            errors.Add($"'{field.Name}' is required, and the server fills it after the request is checked. Make it optional.");
        }

        if ((rule.Prefix ?? string.Empty).Length > MaxPrefixLength)
        {
            errors.Add($"A prefix is at most {MaxPrefixLength} characters.");
        }

        if (!Enum.IsDefined(rule.Strategy) || !Enum.IsDefined(rule.Reset))
        {
            errors.Add("strategy is gapless, sequence or hiLo, and reset is never, yearly, monthly or daily.");
        }

        if (rule.ClientAllocated && rule.Reset != NumberReset.Never)
        {
            errors.Add("A client-allocated series cannot reset.");
        }

        try
        {
            // A sample branch stands in for the code, which is only known per request.
            SeriesFor(definition, rule, "xx").Validate();
        }
        catch (ArgumentException e)
        {
            errors.Add(rule.SeriesTemplate is { Length: > 0 } && e.ParamName == "Name"
                ? "The series template makes a series name that is not valid."
                : e.Message.Split(" (Parameter")[0]);
        }

        return errors;
    }
}
