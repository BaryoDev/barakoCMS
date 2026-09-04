namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Makes a value that a caller chose safe to put on a log line. A newline in it would forge a
/// second entry, an escape sequence would repaint a terminal, and an unbounded length would
/// flood the sink. Prefer logging an identifier of our own; use this when the text itself is
/// what the operator needs to see.
/// </summary>
internal static class LogSafe
{
    public const int DefaultMaxLength = 200;

    /// <summary>
    /// Replaces every control character (C0, DEL and C1, which covers CR, LF, TAB and ESC) with a
    /// space and cuts the result at <paramref name="maxLength"/>, marking the cut with "...".
    /// </summary>
    public static string Text(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        }

        return chars.Length <= maxLength
            ? new string(chars)
            : new string(chars, 0, maxLength) + "...";
    }
}
