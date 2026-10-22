using System.Text.Json;

namespace BarakoCMS.Payments;

internal static class AmountReader
{
    /// <summary>
    /// Reads a stored amount. Only a JSON number or a .NET number counts: text is refused so the
    /// entry's own validation, not a parse here, decides what an amount is.
    /// </summary>
    public static bool TryRead(object? value, out decimal amount)
    {
        amount = 0;
        switch (value)
        {
            case decimal d:
                amount = d;
                return true;
            case int or long or short:
                amount = Convert.ToDecimal(value);
                return true;
            case double or float:
                return decimal.TryParse(
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out amount);
            case JsonElement { ValueKind: JsonValueKind.Number } je:
                return je.TryGetDecimal(out amount);
            default:
                return false;
        }
    }
}
