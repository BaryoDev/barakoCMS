namespace BarakoCMS.Diagnostics;

/// <summary>
/// Pure helpers for normalizing and fingerprinting captured errors. Kept separate from the endpoint
/// so the dedup key and the storage caps can be unit-tested without a host or database.
/// </summary>
public static class ClientErrorText
{
    /// <summary>Trim whitespace and cap length. Null/empty passes through unchanged.</summary>
    public static string? Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>Only "warning" is a warning; anything else (incl. null/garbage) is an error.</summary>
    public static string NormalizeSeverity(string? s) => s == "warning" ? "warning" : "error";

    /// <summary>
    /// A stable 32-char hex key for a fault. The same (kind, message, source, status) always yields
    /// the same fingerprint so recurrences group together; any change yields a different one.
    /// </summary>
    public static string Fingerprint(string kind, string message, string? source, int? status)
        => Incidents.IncidentFingerprint.Of(kind, message, source, status?.ToString());
}
