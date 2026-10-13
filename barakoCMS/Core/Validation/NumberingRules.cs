using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>A content type's <see cref="NumberingRule"/>: what it may say, and the series it counts in.</summary>
internal static class NumberingRules
{
    public const int MaxPrefixLength = 20;

    private static readonly string[] TextTypes = ["string", "text"];

    /// <summary>The series a type's entries are numbered in: the type's own, so a rename of the field keeps counting.</summary>
    public static NumberSeries SeriesFor(ContentTypeDefinition definition, NumberingRule rule)
    {
        var name = "type." + definition.Name.ToLowerInvariant();
        return new NumberSeries(name.Length > 64 ? name[..64] : name, rule.Prefix, rule.Padding, rule.Reset, rule.TimeZoneId);
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

        try
        {
            SeriesFor(definition, rule).Validate();
        }
        catch (ArgumentException e)
        {
            errors.Add(e.Message.Split(" (Parameter")[0]);
        }

        return errors;
    }
}
