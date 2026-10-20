namespace BarakoCMS.Qr;

/// <summary>
/// Which fields may be rendered as a QR code. Bound from <c>Modules:Qr</c>.
/// </summary>
/// <remarks>
/// Empty by default, so installing the module exposes nothing. A field is also served only when
/// delivery already serves it: the entry has to be published and public, and the field has to be
/// marked Public on its content type.
/// </remarks>
public sealed class QrOptions
{
    /// <summary>
    /// Content type name to the field names that may be encoded, both matched case-insensitively.
    /// <c>Modules:Qr:Fields:registration:0=ClaimToken</c>.
    /// </summary>
    public Dictionary<string, List<string>> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest value that is encoded, in characters. A longer value answers 404.</summary>
    public int MaxValueLength { get; set; } = 1024;

    /// <summary>The most pixels one QR module may take. Larger requests are refused with 400.</summary>
    public int MaxPixelsPerModule { get; set; } = 20;

    internal bool Allows(string contentType, string field) =>
        Fields.FirstOrDefault(p => string.Equals(p.Key, contentType, StringComparison.OrdinalIgnoreCase)).Value
            is { } allowed
        && allowed.Any(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
}
