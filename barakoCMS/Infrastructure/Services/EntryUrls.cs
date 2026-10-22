using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Where a tenant's public site lives, and the address of an entry on it. One answer for the feed,
/// the sitemap and the <c>links.site</c> and <c>links.public</c> template placeholders.
/// </summary>
/// <remarks>
/// The origin is the <c>Url</c> of the tenant's published <c>site</c> entry, so two tenants on one
/// deployment link to their own sites. A tenant that sets none falls back to <c>Feeds:SiteUrl</c>
/// and then <c>App:BaseUrl</c>, which is what every tenant's feed linked to before, so a deployment
/// that only sets <c>Feeds:SiteUrl</c> builds the same links it always did.
/// </remarks>
internal static class EntryUrls
{
    public const string FallbackKey = "Feeds:SiteUrl";

    /// <summary>
    /// The <c>Url</c> of the session tenant's published <c>site</c> entry, as an absolute http or
    /// https base with no trailing slash, or null when there is none or it is not such a URL.
    /// </summary>
    /// <remarks>
    /// The oldest published entry, as the template placeholders always read it. A value with a query
    /// or a fragment is passed over, since a path appended to it would land inside either.
    /// </remarks>
    public static async Task<string?> TenantOriginAsync(IQuerySession session, CancellationToken ct)
    {
        var site = await session.Query<Content>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        return AbsoluteBase(Setting(site, "Url"));
    }

    /// <summary>
    /// The tenant's origin, else the deployment's (<see cref="FallbackKey"/>, then <c>App:BaseUrl</c>,
    /// then a request host that host filtering vouches for), else null.
    /// </summary>
    /// <exception cref="InvalidOperationException">A configured fallback is not an absolute http(s) URL.</exception>
    public static async Task<string?> OriginAsync(
        IQuerySession session, IConfiguration config, HttpRequest request, CancellationToken ct) =>
        await TenantOriginAsync(session, ct)
        ?? barakoCMS.Infrastructure.Security.CanonicalHost.BaseUrl(config, request, FallbackKey);

    /// <summary>
    /// The deployment's origin from configuration alone, for a caller with no request:
    /// <see cref="FallbackKey"/>, then <c>App:BaseUrl</c>. Null when neither is a usable URL.
    /// </summary>
    public static string? ConfiguredOrigin(IConfiguration config) =>
        AbsoluteBase(config[FallbackKey])
        ?? AbsoluteBase(config[barakoCMS.Infrastructure.Security.CanonicalHost.BaseUrlKey]);

    /// <summary>
    /// The public address of <paramref name="entry"/>, or null when delivery would not serve it at a
    /// slug: its type is not deliverable or has no Public slug field, the entry is not Published and
    /// Public, or no origin is known.
    /// </summary>
    /// <remarks>
    /// Asked of the delivery projection, so an address is only built for an entry an anonymous
    /// reader could open there, and a slug delivery does not serve never reaches a message.
    /// </remarks>
    public static async Task<string?> PublicUrlAsync(
        IQuerySession session, Content entry, string? tenantOrigin, IConfiguration config, CancellationToken ct)
    {
        var origin = AbsoluteBase(tenantOrigin) ?? ConfiguredOrigin(config);
        if (origin is null)
        {
            return null;
        }

        var type = entry.ContentType;
        var def = await session.Query<ContentTypeDefinition>().FirstNamedAsync(type, ct);
        var slugField = def is null ? null : barakoCMS.Features.Public.PublicDelivery.SlugField(def);
        if (slugField is null
            || barakoCMS.Features.Public.PublicDelivery.ToPublic(entry, def, slugField) is not { Slug: { Length: > 0 } slug })
        {
            return null;
        }

        return EntryUrl(origin, def!, slug, config);
    }

    /// <summary>The absolute address of the entry with this slug on <paramref name="origin"/>.</summary>
    public static string EntryUrl(string origin, ContentTypeDefinition def, string slug, IConfiguration config) =>
        origin + barakoCMS.Features.Public.PublicDelivery.PathTemplate(def, config)
            .Replace("{slug}", Uri.EscapeDataString(slug));

    /// <summary>An absolute http or https URL with no query or fragment, without its trailing slash, or null.</summary>
    public static string? AbsoluteBase(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return text.TrimEnd('/');
    }

    /// <summary>A text setting on the site entry, matched by name ignoring case, or null when it is blank.</summary>
    public static string? Setting(Content? site, string name)
    {
        if (site?.Data is null)
        {
            return null;
        }

        foreach (var (key, value) in site.Data)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                var text = value?.ToString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        return null;
    }
}
