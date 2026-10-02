namespace barakoCMS.Features.ContentType.SetRouteTemplate;

internal class Request
{
    /// <summary>
    /// Where an entry of this type lives on the site, as a path holding <c>{slug}</c> once, such as
    /// <c>/blog/{slug}</c>. Null clears it, and the feed and the sitemap go back to
    /// <c>Feeds:Paths:{type}</c> and then <c>/{type}/{slug}</c>.
    /// </summary>
    public string? RouteTemplate { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The template now declared, or null when the type has none.</summary>
    public string? RouteTemplate { get; set; }
}
