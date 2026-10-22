using System.Globalization;

namespace barakoCMS.Infrastructure.Rules;

/// <summary>The comparison ops a rule condition uses.</summary>
internal static class RuleConditions
{
    /// <summary>A sign and a decimal point only: no thousands separators, exponents or spaces.</summary>
    private const NumberStyles Numbers = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    /// <summary>Reads a number the way every rule reads one.</summary>
    internal static bool TryNumber(string? text, out decimal value) =>
        decimal.TryParse(text, Numbers, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// eq and ne ignore case, gt to lte compare as numbers, then as dates, then as text
    /// (see Order), in reads a comma list. A missing side reads as empty.
    /// </summary>
    internal static bool Compare(string op, string? left, string? right)
    {
        var l = left ?? "";
        var r = right ?? "";
        switch (op.ToLowerInvariant())
        {
            case "eq": return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
            case "ne": return !string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
            case "empty": return l.Length == 0;
            case "notempty": return l.Length > 0;
            case "in": return r.Split(',', StringSplitOptions.TrimEntries).Contains(l, StringComparer.OrdinalIgnoreCase);
            case "gt" or "gte" or "lt" or "lte":
                var order = Order(l, r);
                if (order is null) return false;
                return op.ToLowerInvariant() switch { "gt" => order > 0, "gte" => order >= 0, "lt" => order < 0, _ => order <= 0 };
            default: return false;
        }
    }

    /// <summary>
    /// Numbers as numbers, then dates as instants (no offset means UTC), then text. A number or a
    /// date against something that is not one has no order: "abc" must not pass "gt 0" or
    /// "gt $NOW" by sorting after it.
    /// </summary>
    private static int? Order(string l, string r)
    {
        var (leftNumber, rightNumber) = (TryNumber(l, out var a), TryNumber(r, out var b));
        if (leftNumber && rightNumber) return a.CompareTo(b);
        if (leftNumber || rightNumber) return null;

        var leftDate = DateTimeOffset.TryParse(l, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var x);
        var rightDate = DateTimeOffset.TryParse(r, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var y);
        if (leftDate && rightDate) return x.CompareTo(y);
        if (leftDate || rightDate) return null;

        return string.CompareOrdinal(l, r);
    }
}
