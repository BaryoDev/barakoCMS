using System.Globalization;

namespace barakoCMS.Core.Expressions;

internal enum ExprType
{
    Null,
    Number,
    Text,
    Bool,
    Date,
}

/// <summary>A name a formula reads: a field of the entry, or one of the fixed system names.</summary>
internal readonly record struct NameRef(string Name, bool IsSystem);

/// <summary>Maps a field name to its type, or null when the type does not declare it.</summary>
internal delegate ExprType? NameResolver(string field);

/// <summary>Gives the value of a name: a decimal, string, bool, DateTime or null, or a number, date or offset that converts to one.</summary>
internal delegate object? ValueResolver(NameRef name);

internal static class Values
{
    public static ExprType TypeOf(object? value) => value switch
    {
        decimal => ExprType.Number,
        string => ExprType.Text,
        bool => ExprType.Bool,
        DateTime => ExprType.Date,
        _ => ExprType.Null,
    };

    /// <summary>Converts what a resolver gave to one of the five value types, or says it cannot.</summary>
    public static bool TryNormalize(object? raw, out object? value)
    {
        value = null;
        switch (raw)
        {
            case null:
                return true;
            case decimal or string or bool or DateTime:
                value = raw;
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                return true;
            case double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28:
                value = (decimal)d;
                return true;
            case float f when float.IsFinite(f):
                value = (decimal)f;
                return true;
            case DateTimeOffset offset:
                value = offset.UtcDateTime;
                return true;
            case DateOnly day:
                value = day.ToDateTime(TimeOnly.MinValue);
                return true;
            default:
                return false;
        }
    }

    public static string Word(ExprType type) => type switch
    {
        ExprType.Number => "number",
        ExprType.Text => "text",
        ExprType.Bool => "bool",
        ExprType.Date => "date",
        _ => "null",
    };

    public static bool IsBlank(object? value) => value is null || (value is string s && string.IsNullOrWhiteSpace(s));
}
