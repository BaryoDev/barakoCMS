using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Resolves the absolute base URL for anything a third party will read back: an RSS item link, an
/// OAuth <c>redirect_uri</c>, a link in an email.
/// </summary>
/// <remarks>
/// <para>
/// <c>Request.Host</c> is the caller's <c>Host</c> header. ASP.NET Core's host filtering is what
/// makes it trustworthy, and it is only doing anything when <c>AllowedHosts</c> names real hosts.
/// The shipped default is <c>"*"</c>, which accepts everything, so building a URL from the header
/// under that setting lets the caller choose the origin of every link in the response.
/// </para>
/// <para>
/// Configuration therefore comes first, and the request host is a fallback only where host filtering
/// has already rejected everything else. With neither, there is no answer, and the caller fails
/// closed rather than guessing. Validating the header instead would mean writing a second, weaker
/// copy of the host filter that lives next to the code that trusts it. See issue #147.
/// </para>
/// </remarks>
public static class CanonicalHost
{
    /// <summary>The host-filtering setting, read here to decide whether the request host is evidence.</summary>
    public const string AllowedHostsKey = "AllowedHosts";

    /// <summary>The deployment's own public URL, and the answer whenever it is set.</summary>
    public const string BaseUrlKey = "App:BaseUrl";

    /// <summary>
    /// True when <c>AllowedHosts</c> restricts the <c>Host</c> header to a known set, so a request
    /// that reached the application carries a host somebody chose.
    /// </summary>
    /// <remarks>
    /// Unset or empty turns host filtering off entirely, and a bare <c>*</c> entry matches every
    /// host, so both mean the same thing here. A wildcard subdomain such as <c>*.example.com</c> is
    /// still a restriction and still counts.
    /// </remarks>
    public static bool IsHostFilteringConstrained(string? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            return false;
        }

        var entries = allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return entries.Length > 0 && !entries.Contains("*");
    }

    /// <summary>
    /// The canonical base URL with no trailing slash, or null when nothing configured one and the
    /// request host cannot be believed.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="request">The current request, used only when host filtering constrains it.</param>
    /// <param name="preferredKey">
    /// A setting checked before <see cref="BaseUrlKey"/>, for a feature that names its own public URL
    /// (<c>Feeds:SiteUrl</c> points at the frontend, which is a different host from the API).
    /// </param>
    /// <exception cref="InvalidOperationException">A configured value is not an absolute http(s) URL.</exception>
    public static string? BaseUrl(IConfiguration configuration, HttpRequest request, string? preferredKey = null)
    {
        var configured = Configured(configuration, preferredKey);
        if (configured is not null)
        {
            return configured;
        }

        if (!IsHostFilteringConstrained(configuration[AllowedHostsKey]) || !request.Host.HasValue)
        {
            return null;
        }

        return $"{request.Scheme}://{request.Host}".TrimEnd('/');
    }

    /// <summary>The message a caller uses when <see cref="BaseUrl"/> came back null.</summary>
    public static string NotConfigured(string settingKey) =>
        $"No canonical base URL is configured. Set {settingKey} to this deployment's public URL, or set "
      + $"{AllowedHostsKey} to the hosts it answers on. The Host header is written by the caller, so it "
      + "is not used to build links while AllowedHosts accepts every host.";

    private static string? Configured(IConfiguration configuration, string? preferredKey)
    {
        var keys = preferredKey is null || preferredKey == BaseUrlKey
            ? new[] { BaseUrlKey }
            : new[] { preferredKey, BaseUrlKey };

        foreach (var key in keys)
        {
            var value = configuration[key]?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            // A relative or malformed value produces links that resolve against whatever fetched the
            // document, which is the problem this class exists to remove, quietly reintroduced by a
            // typo. Refusing names the setting while the deploy is still in front of somebody.
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"{key} is '{value}', which is not an absolute http or https URL such as https://example.com.");
            }

            return value.TrimEnd('/');
        }

        return null;
    }
}
