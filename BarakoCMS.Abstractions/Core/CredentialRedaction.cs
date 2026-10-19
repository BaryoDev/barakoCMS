using System.Text.RegularExpressions;

namespace barakoCMS.Core;

/// <summary>
/// Strips credentials and identifiers from failure text before it is stored or sent on.
/// </summary>
/// <remarks>
/// <para>
/// The rules the diagnostics module applies to incident text, kept here so the core can apply them
/// where it stores an error: the core may not reference a module. Free text a provider wrote can
/// carry a URL with a token in it, a header, a form value or an address, and pattern rules cannot
/// know every secret, so they lean towards removing too much:
/// </para>
/// <list type="bullet">
/// <item>JSON web tokens, and the value after <c>Bearer</c> or <c>Basic</c>.</item>
/// <item>Every query string value, and the user and password in a URL.</item>
/// <item>The value after a key named like a credential (password, secret, token, api key, access
/// key, private key, auth (but not unauthorized), session, cookie, signature, otp).</item>
/// <item>Email addresses, and any unbroken run of 32 or more letters, digits, <c>-</c> or <c>_</c>
/// holding at least one letter and one digit.</item>
/// </list>
/// </remarks>
public static class CredentialRedaction
{
    public const string Mask = "[redacted]";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // "auth" matches as a substring (authKey, basicAuth, oauth) except after "un", so Unauthorized
    // and Unauthenticated keep the reason that follows them.
    private const string CredentialNames =
        @"(?:[\w-]*(?:password|passwd|pwd|secret|token|api[_\s-]?key|access[_\s-]?key|private[_\s-]?key|authorization|session|cookie|signature|otp)[\w-]*"
        + @"|[\w-]*(?<!un)auth[\w-]*)";

    // A value: a double-quoted string with escapes, an escaped-JSON string, a single-quoted string,
    // or bare text up to a delimiter. An already-masked value is left alone.
    private const string Value =
        @"(?!\[redacted\])(?:""(?:[^""\\]|\\.)*""|\\""(?:[^\\]|\\(?!""))*\\""|'[^']*'|[^,;&}\]\r\n]+)";

    private static readonly Regex Jwt = new(
        @"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*", Options, Timeout);

    private static readonly Regex AuthScheme = new(
        @"\b(bearer|basic)\s+[A-Za-z0-9._~+/=-]{6,}", Options, Timeout);

    private static readonly Regex UrlUserInfo = new(
        @"(://)[^/\s:@]+(:[^/\s@]*)?@", Options, Timeout);

    private static readonly Regex QueryValue = new(
        @"([?&][^=&#\s""'<>]+=)[^&#\s""'<>\]]*", Options, Timeout);

    private static readonly Regex CredentialPair = Pair(CredentialNames);

    private static readonly Regex Email = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", Options, Timeout);

    private static readonly Regex OpaqueRun = new(
        @"(?<![A-Za-z0-9_-])(?=[A-Za-z0-9_-]{32})(?=[A-Za-z_-]*[0-9])(?=[0-9_-]*[A-Za-z])[A-Za-z0-9_-]{32,}", Options, Timeout);

    /// <summary>Applies every rule. Null and empty pass through.</summary>
    public static string? Redact(string? text) =>
        RedactIdentifiers(RedactCredentials(text));

    /// <summary>Applies the rules for tokens, URLs and credential-named values.</summary>
    public static string? RedactCredentials(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var s = text;
            s = Jwt.Replace(s, Mask);
            s = AuthScheme.Replace(s, m => $"{m.Groups[1].Value} {Mask}");
            s = UrlUserInfo.Replace(s, m => $"{m.Groups[1].Value}{Mask}@");
            s = QueryValue.Replace(s, m => m.Groups[1].Value + Mask);
            return CredentialPair.Replace(s, m => m.Groups[1].Value + Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // Text built to make a rule slow is not text worth keeping.
            return Mask;
        }
    }

    /// <summary>Applies the rules for email addresses and opaque identifiers.</summary>
    public static string? RedactIdentifiers(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            return OpaqueRun.Replace(Email.Replace(text, Mask), Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }

    /// <summary>
    /// A rule that masks the value after any of <paramref name="names"/> used as a key, or null when
    /// there are none. For a caller with names of its own, such as fields a tenant marked Sensitive.
    /// </summary>
    public static Regex? PairFor(IEnumerable<string> names)
    {
        var escaped = names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Regex.Escape)
            .ToList();

        return escaped.Count == 0 ? null : Pair($"(?:{string.Join("|", escaped)})");
    }

    // The key may be quoted, plainly or JSON-escaped, and is followed by : or =.
    private static Regex Pair(string keys) => new(
        $@"((?:\\?[""'])?\b{keys}\b(?:\\?[""'])?\s*[:=]\s*){Value}",
        Options, Timeout);
}
