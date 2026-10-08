using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Cuts every absolute URL in a piece of text down to its scheme and host, the way a workflow run's
/// error already shows a webhook URL.
/// </summary>
/// <remarks>
/// For text nobody here composed, such as an exception message. Some webhook URLs carry a token in
/// the path or the query, and an <see cref="HttpRequestException"/> can quote the URL it was sent
/// to, so the message is not safe to store or hand out as it stands. The host is kept so a reader
/// can still tell one integration from another.
/// </remarks>
internal static partial class UrlRedaction
{
    public static string InText(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
            return text ?? string.Empty;

        try
        {
            return AbsoluteUrl().Replace(text, match => Uri.TryCreate(match.Value, UriKind.Absolute, out _)
                ? barakoCMS.Features.Workflows.Actions.WebhookAction.Redact(match.Value)
                : "(a URL)");
        }
        catch (RegexMatchTimeoutException)
        {
            return "(the text held a URL and could not be redacted)";
        }
    }

    /// <summary>A scheme, then everything up to whitespace, a quote or a bracket.</summary>
    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://[^\s""'<>(){}]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex AbsoluteUrl();
}
