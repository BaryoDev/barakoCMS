namespace barakoCMS.Models;

/// <summary>
/// One old path that should send a visitor to a new one.
/// </summary>
/// <remarks>
/// Editorial knowledge rather than infrastructure. Which URLs moved is something the person who
/// rebuilt the site knows and the person who runs nginx does not, so it lives with the content.
///
/// Paths, not URLs. A redirect is within a site: storing an absolute URL would make this an open
/// redirector, where anyone who can add a rule can point a trusted domain at their own. Both ends
/// are normalised to a leading slash and no trailing one, so "/about", "about" and "/about/" are the
/// same rule and cannot be entered as three.
/// </remarks>
public class UrlRedirect
{
    public Guid Id { get; set; }

    /// <summary>The path that no longer exists, normalised. Unique per tenant.</summary>
    public string FromPath { get; set; } = string.Empty;

    /// <summary>Where to send it, normalised.</summary>
    public string ToPath { get; set; } = string.Empty;

    /// <summary>
    /// 301 when true, 302 when false.
    /// </summary>
    /// <remarks>
    /// Defaults to false, and that is the safe direction rather than the common one. A browser caches
    /// a 301 indefinitely and will not ask again, so a permanent redirect entered by mistake is not
    /// fixed by deleting the rule: every visitor who saw it keeps following it. Temporary is
    /// recoverable, permanent is what you choose once you are sure.
    /// </remarks>
    public bool Permanent { get; set; }

    /// <summary>Free text, so somebody later knows why this exists.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The stored form of a path: one leading slash, no trailing slash, query and fragment dropped.
    /// </summary>
    /// <remarks>
    /// Case is preserved rather than lowered. Paths are case sensitive on most servers and a rule
    /// that silently matched a different case would be a rule nobody wrote. Lookup compares exactly,
    /// so what is stored is what matches.
    ///
    /// The query string is dropped because a redirect is about the path. Keeping it would make
    /// "/old?utm_source=x" a different rule from "/old", and a migration would need one per campaign.
    /// </remarks>
    public static string Normalize(string? path)
    {
        var value = (path ?? string.Empty).Trim();

        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0) value = value[..cut];

        value = value.Trim();
        if (value.Length == 0) return "/";

        if (!value.StartsWith('/')) value = "/" + value;

        // Collapse repeated slashes, so "//about" and "/about" are one rule rather than two that
        // look identical in a list.
        while (value.Contains("//", StringComparison.Ordinal))
        {
            value = value.Replace("//", "/", StringComparison.Ordinal);
        }

        return value.Length > 1 ? value.TrimEnd('/') : "/";
    }
}
