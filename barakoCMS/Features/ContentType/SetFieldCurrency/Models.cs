namespace barakoCMS.Features.ContentType.SetFieldCurrency;

internal class Request
{
    /// <summary>
    /// The ISO 4217 code the field's amounts are in from now on, three capital letters. Null clears
    /// it, and the field goes back to holding a plain number.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// The most decimal places an amount may carry, from 0 to 8. Null takes the currency's own, and
    /// a code outside the built-in list needs one.
    /// </summary>
    public int? Scale { get; set; }

    /// <summary>
    /// Required when entries hold an amount the new scale refuses, or when the currency changes
    /// from one code to another on a field entries hold amounts in. Ignored otherwise.
    /// </summary>
    /// <remarks>
    /// No entry is rewritten and nothing is converted. An entry holding an amount that does not fit
    /// is refused on its next save until the amount is corrected, and an amount stored under the old
    /// code is read under the new one. The refusal names how many entries each is.
    /// </remarks>
    public bool Force { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;
    public string Field { get; set; } = string.Empty;

    /// <summary>The code now declared, or null when the field holds a plain number.</summary>
    public string? Currency { get; set; }

    /// <summary>The decimal places now applied on a write, or null when the field holds a plain number.</summary>
    public int? Scale { get; set; }

    /// <summary>Entries holding a value the declared scale refuses. Zero unless force was set.</summary>
    public int EntriesNotFitting { get; set; }

    /// <summary>Entries holding an amount stored under another code. Zero unless force was set.</summary>
    public int EntriesRelabelled { get; set; }
}
