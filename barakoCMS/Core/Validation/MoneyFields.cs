using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a <c>money</c> field that declares a <see cref="FieldDefinition.Currency"/> accepts, checked
/// once when the type is saved and applied to every entry write.
/// </summary>
/// <remarks>
/// A money field with no currency is a plain number and nothing here applies to it, so a type
/// stored before currencies existed keeps accepting what it accepted.
///
/// The rounding rule is that there is none. An amount with more decimal places than the scale
/// allows is refused on write, and nothing is rounded on write or on read. Two paths cannot disagree
/// about a total when neither of them rounds.
///
/// The amount stays a plain JSON number in the entry. The currency lives on the definition, so a
/// filter, a sort, an export and every reader of a stored entry see the same value they always saw.
///
/// The scale comes from a fixed table of ISO 4217 codes and their minor units, compiled in: no
/// lookup leaves the process. The table goes stale when a currency is introduced, so a code it does
/// not hold is still accepted when the field declares its own scale.
///
/// A stored field whose currency cannot be resolved (hand edited, or stored some other way) is read
/// as a plain number instead of failing every write to its type.
/// </remarks>
internal static class MoneyFields
{
    public const int MaxScale = 8;

    // ISO 4217 minor units. Codes with no minor unit (XAU, XDR, XTS and the like) are left out, and
    // a field naming one declares its own scale. ANG (replaced by XCG in 2025) and BGN (replaced by
    // EUR in 2026) stay, because amounts stored before the change are still in them.
    private static readonly Dictionary<string, int> MinorUnits = BuildMinorUnits();

    private static Dictionary<string, int> BuildMinorUnits()
    {
        var units = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(int scale, string codes)
        {
            foreach (var code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                units[code] = scale;
        }

        Add(0, "BIF CLP DJF GNF ISK JPY KMF KRW PYG RWF UGX UYI VND VUV XAF XOF XPF");
        Add(3, "BHD IQD JOD KWD LYD OMR TND");
        Add(4, "CLF UYW");
        Add(2, "AED AFN ALL AMD ANG AOA ARS AUD AWG AZN BAM BBD BDT BGN BMD BND BOB BOV BRL BSD BTN BWP "
             + "BYN BZD CAD CDF CHE CHF CHW CNY COP COU CRC CUP CVE CZK DKK DOP DZD EGP ERN ETB EUR FJD "
             + "FKP GBP GEL GHS GIP GMD GTQ GYD HKD HNL HTG HUF IDR ILS INR IRR JMD KES KGS KHR KPW KYD "
             + "KZT LAK LBP LKR LRD LSL MAD MDL MGA MKD MMK MNT MOP MRU MUR MVR MWK MXN MXV MYR MZN NAD "
             + "NGN NIO NOK NPR NZD PAB PEN PGK PHP PKR PLN QAR RON RSD RUB SAR SBD SCR SDG SEK SGD SHP "
             + "SLE SOS SRD SSP STN SVC SYP SZL THB TJS TMT TOP TRY TTD TWD TZS UAH USD USN UYU UZS VED "
             + "VES WST XCD XCG YER ZAR ZMW ZWG");

        return units;
    }

    public static bool IsMoney(string? type) =>
        string.Equals(type, "money", StringComparison.OrdinalIgnoreCase);

    /// <summary>The minor unit of a code the built-in table holds, or null.</summary>
    public static int? MinorUnitOf(string? currency) =>
        currency is not null && MinorUnits.TryGetValue(currency, out var scale) ? scale : null;

    /// <summary>What is wrong with the currency and scale a field declares, for the type validator.</summary>
    public static List<string> DefinitionErrors(FieldDefinition field)
    {
        var errors = new List<string>();
        if (field.Currency is null && field.Scale is null)
            return errors;

        if (!IsMoney(field.Type))
        {
            errors.Add($"Field '{field.Name}' declares a currency or a scale but is of type '{field.Type}', not money.");
            return errors;
        }

        if (field.Currency is null)
        {
            errors.Add($"Field '{field.Name}' declares a scale and no currency. A scale is the decimal "
                + "places of an amount in a declared currency.");
            return errors;
        }

        // Capitals only, so one spelling is stored and compared everywhere. Not echoed here: what
        // was sent is not known to be three letters yet.
        if (!IsCode(field.Currency))
        {
            errors.Add($"Field '{field.Name}' has a currency that is not three capital letters, such as USD.");
            return errors;
        }

        if (field.Scale is { } scale)
        {
            if (scale < 0 || scale > MaxScale)
                errors.Add($"Field '{field.Name}' has a scale of {scale}, and a scale is a whole number from 0 to {MaxScale}.");
        }
        else if (MinorUnitOf(field.Currency) is null)
        {
            errors.Add($"Field '{field.Name}' names the currency '{field.Currency}', which the built-in "
                + $"ISO 4217 list does not hold, so its decimal places are not known. Declare a scale from 0 to {MaxScale} to use it.");
        }

        return errors;
    }

    /// <summary>
    /// The currency and scale an entry write applies to this field, or false for a field that
    /// declares none or stores one that a save would refuse.
    /// </summary>
    public static bool TryResolve(FieldDefinition field, out string currency, out int scale)
    {
        currency = string.Empty;
        scale = 0;

        if (!IsMoney(field.Type) || field.Currency is null || !IsCode(field.Currency))
            return false;

        if (field.Scale is { } declared)
        {
            if (declared < 0 || declared > MaxScale)
                return false;

            scale = declared;
        }
        else if (MinorUnitOf(field.Currency) is { } unit)
        {
            scale = unit;
        }
        else
        {
            return false;
        }

        currency = field.Currency;
        return true;
    }

    /// <summary>
    /// Why a present value cannot be stored in this field, or null. Null for a field with no
    /// currency, whatever the value.
    /// </summary>
    public static string? ValueError(FieldDefinition field, object value)
    {
        if (!TryResolve(field, out var currency, out var scale))
            return null;

        var label = $"Field '{field.DisplayName}' ({field.Name})";

        if (AsExact(value) is not { } amount)
            return $"{label} holds an amount in {currency} and takes a JSON number a decimal can hold "
                + "exactly. Text, and a number outside that range, are refused.";

        // The amount is not named. Write-path sensitivity puts the stored value of a field the
        // caller may not see back into the data before this runs, so naming it would hand that
        // value to the caller.
        if (!Fits(amount, scale))
            return $"{label} holds an amount in {currency} with at most {scale} decimal "
                + $"{(scale == 1 ? "place" : "places")}, and this one has more. An amount is never "
                + "rounded for you, so send it already rounded.";

        return null;
    }

    /// <summary>
    /// Reads plain decimal text as an amount for this field, for a writer that only has text: a
    /// spreadsheet cell, a workflow parameter, a form input. False for a field with no usable
    /// currency, and for anything that is not plain decimal text.
    /// </summary>
    /// <remarks>
    /// The API itself stays strict and takes a JSON number. This is for the writers that cannot
    /// send one, so what they store is a number like every other writer's. The scale is not checked
    /// here; <see cref="ValueError"/> does that on the amount this returns.
    /// </remarks>
    public static bool TryReadAmountText(FieldDefinition field, object? value, out decimal amount)
    {
        amount = 0;

        var text = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null,
        };

        return text is not null && TryResolve(field, out _, out _) && TryParsePlain(text, out amount);
    }

    /// <summary>
    /// An optional sign, digits, and at most one point with digits on both sides. No exponent, no
    /// thousands separator, no currency symbol, no space inside. Space around it is ignored.
    /// </summary>
    public static bool TryParsePlain(string text, out decimal amount)
    {
        amount = 0;
        var trimmed = text.Trim();

        var digits = 0;
        var points = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is >= '0' and <= '9')
                digits++;
            else if (c == '.' && points == 0 && digits > 0 && i < trimmed.Length - 1)
                points++;
            else if ((c == '-' || c == '+') && i == 0)
                continue;
            else
                return false;
        }

        return digits > 0
            && decimal.TryParse(
                trimmed,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount);
    }

    /// <summary>Whether the amount has no digit past the scale. Trailing zeros do not count.</summary>
    public static bool Fits(decimal amount, int scale) => decimal.Round(amount, scale) == amount;

    // A double or a float is refused even when it happens to be a round number: the request
    // converter only produces one for a number a decimal cannot hold, and a caller in process that
    // passes one has already been through binary floating point.
    private static decimal? AsExact(object value) => value switch
    {
        decimal m => m,
        long l => l,
        int i => i,
        short sh => sh,
        byte by => by,
        JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetDecimal(out var parsed) => parsed,
        _ => null,
    };

    private static bool IsCode(string value) =>
        value.Length == 3 && value.All(c => c is >= 'A' and <= 'Z');
}
