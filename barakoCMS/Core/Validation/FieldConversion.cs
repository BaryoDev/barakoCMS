using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a field change of kind convert does to one stored value, and to the field itself.
/// </summary>
/// <remarks>
/// A value is read as text, looked up in the change's mapping, and kept when what comes out is a
/// value of the new type (an option of the new choice, for a choice). Anything else is dropped,
/// which is why a convert that would drop a value counts as destructive and needs the flag.
/// </remarks>
internal static class FieldConversion
{
    /// <summary>The types a field may be converted to.</summary>
    public static IReadOnlyList<string> Targets { get; } =
        ["string", "text", "markdown", "int", "decimal", "bool", "date", "datetime", "email", "url", "slug", "choice"];

    /// <summary>The value a stored one becomes, or null when it does not survive the convert.</summary>
    public static object? Convert(object? value, FieldChange change)
    {
        if (value is null || AsText(value) is not { } text)
        {
            return null;
        }

        if (change.Mapping is not null && change.Mapping.TryGetValue(text, out var mapped))
        {
            text = mapped;
        }

        var target = change.ToType!.ToLowerInvariant();
        object candidate = target switch
        {
            "int" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
            "decimal" when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            "bool" when bool.TryParse(text, out var b) => b,
            _ => text,
        };

        if (target == "choice")
        {
            return change.Options?.Any(o => string.Equals(o.Value, text, StringComparison.Ordinal)) == true ? text : null;
        }

        return FieldTypeRegistry.IsValidValue(target, candidate) ? candidate : null;
    }

    /// <summary>The field as it is once converted.</summary>
    public static void Retype(FieldDefinition field, FieldChange change)
    {
        field.Type = change.ToType!;
        field.Options = string.Equals(change.ToType, "choice", StringComparison.OrdinalIgnoreCase) ? change.Options : null;
        field.Multiple = false;
        field.ReferenceType = null;
        field.Currency = null;
        field.Scale = null;
        field.TokenLength = null;
        field.ValidationRules = new Dictionary<string, object>();
    }

    /// <summary>A scalar value as text, or null for a list or an object, which no convert takes.</summary>
    private static string? AsText(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f when value is not System.Collections.IEnumerable => f.ToString(null, CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } je => je.GetRawText(),
        _ => null,
    };
}
