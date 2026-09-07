namespace barakoCMS.Infrastructure.Logging;

/// <summary>
/// Makes a caller-supplied value safe to put in a log line.
/// </summary>
/// <remarks>
/// <para>
/// A request path, a tenant slug and a username are all written by whoever sent the request.
/// ASP.NET URL-decodes the path before anything reads it, so a request for <c>/a%0AFATAL%20bogus</c>
/// arrives as a value with a real newline in it. Written to a text sink that is one line per entry,
/// that value becomes two entries, the second one forged: an attacker chooses what the log appears
/// to say, which is the point of CWE-117.
/// </para>
/// <para>
/// Structured logging already helps, because the value travels as a property rather than being
/// concatenated into the template, and a JSON sink escapes it. This does not rely on the sink: the
/// same code runs against a console in development and whatever an operator configures in
/// production, and the safe assumption is the plainest one.
/// </para>
/// <para>
/// Control characters become a single space rather than being dropped, so the value still reads as
/// the length it was, and the result is capped: a log line is not the place for an unbounded string.
/// </para>
/// </remarks>
internal static class LogSafe
{
    public const int MaxLength = 256;

    /// <summary>Strips control characters and caps the length.</summary>
    public static string Value(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var capped = value.Length > MaxLength ? value[..MaxLength] : value;
        Span<char> buffer = capped.Length <= 256 ? stackalloc char[capped.Length] : new char[capped.Length];
        for (var i = 0; i < capped.Length; i++)
        {
            var c = capped[i];
            buffer[i] = char.IsControl(c) ? ' ' : c;
        }

        var cleaned = new string(buffer);
        return value.Length > MaxLength ? cleaned + "..." : cleaned;
    }
}
