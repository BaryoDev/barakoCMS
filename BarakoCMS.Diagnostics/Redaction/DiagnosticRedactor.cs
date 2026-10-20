using System.Text.RegularExpressions;
using barakoCMS.Core;

namespace BarakoCMS.Diagnostics.Redaction;

/// <summary>
/// Strips what must not leave the API from failure text: the incidents view (#893) and the
/// forwarder (#627) both send text through here, so there is one set of rules to read and test.
/// </summary>
/// <remarks>
/// <para>
/// A recorded error is free text a browser or a remote server wrote, so it can carry a URL with a
/// token in it, a header, a form value or a customer's address (the class of leak in #598 and
/// #608). Pattern rules cannot know every secret, so the rules lean towards removing too much:
/// </para>
/// <list type="bullet">
/// <item>JSON web tokens, and the value after <c>Bearer</c> or <c>Basic</c>.</item>
/// <item>Every query string value, and the user and password in a URL.</item>
/// <item>The value after a key named like a credential (password, secret, token, api key, access
/// key, private key, auth, session, cookie, signature, otp), or like a content field marked
/// Sensitive or Hidden in the tenant. Quoted, the whole quoted value goes, escapes included;
/// unquoted, everything up to the next comma, semicolon, ampersand, bracket or line end goes,
/// so a value with spaces in it goes whole. Keys inside escaped JSON (<c>\"key\":\"value\"</c>)
/// count too.</item>
/// <item>Email addresses, and any unbroken run of 32 or more letters, digits, <c>-</c> or <c>_</c>
/// holding at least one letter and one digit, which is what an API key or a session id looks like
/// and a long type name does not.</item>
/// </list>
/// <para>
/// A Sensitive value that appears with no field name in front of it is not recognisable as one
/// and is not removed. The module README says so.
/// </para>
/// </remarks>
internal sealed class DiagnosticRedactor
{
    public const string Mask = CredentialRedaction.Mask;

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static readonly Regex AbsoluteUrl = new(
        @"([A-Za-z][A-Za-z0-9+.\-]*://[^/\s""'<>(){}?#]+)[^\s""'<>(){}]*", Options, Timeout);

    private readonly Regex? _sensitivePair;

    /// <param name="sensitiveFields">Names of fields marked Sensitive or Hidden, whose values go too.</param>
    public DiagnosticRedactor(IReadOnlyCollection<string>? sensitiveFields = null)
    {
        _sensitivePair = CredentialRedaction.PairFor(sensitiveFields ?? []);
    }

    /// <summary>One-off redaction, for a caller with no redactor of its own.</summary>
    public static string? Redact(string? text, IReadOnlyCollection<string>? sensitiveFields = null) =>
        new DiagnosticRedactor(sensitiveFields).Apply(text);

    /// <summary>Redacts <paramref name="text"/>; null and empty pass through.</summary>
    public string? Apply(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            var s = CredentialRedaction.RedactCredentials(text);
            if (_sensitivePair is not null && s is not null)
            {
                s = _sensitivePair.Replace(s, m => m.Groups[1].Value + Mask);
            }
            return CredentialRedaction.RedactIdentifiers(s);
        }
        catch (RegexMatchTimeoutException)
        {
            // Text built to make a rule slow is not text worth showing. Saying nothing beats
            // sending it on unredacted.
            return Mask;
        }
    }

    /// <summary>
    /// Cuts every absolute URL to its scheme and host, as the jobs and workflow-run lists already do
    /// for server-side errors: a webhook URL can carry its secret in the path, which no rule above
    /// recognises. Not used for browser errors, whose script and page paths are what a reader needs.
    /// </summary>
    public static string? CutUrls(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
        {
            return text;
        }
        try
        {
            return AbsoluteUrl.Replace(text, m => m.Groups[1].Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return Mask;
        }
    }
}
