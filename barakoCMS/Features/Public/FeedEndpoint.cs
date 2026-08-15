using System.Globalization;
using System.Text;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public;

/// <summary>
/// GET /api/public/{type}/feed.xml — an RSS 2.0 feed of the newest Published, document-Public entries of
/// a content type. It reuses the same projection as the rest of public delivery, so drafts, Sensitive
/// documents, and non-Public fields never appear. The literal "feed.xml" segment wins over the {slug}
/// route. Anonymous and cacheable.
///
/// Item links point at the caller's frontend (the CMS is headless, so it can't know the URL): set
/// <c>Feeds:SiteUrl</c> (falls back to the request host) and, per type, <c>Feeds:Paths:{type}</c>
/// (a template like <c>/blog/{slug}</c>; defaults to <c>/{type}/{slug}</c>).
/// </summary>
public class FeedEndpoint : EndpointWithoutRequest
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;

    public FeedEndpoint(IQuerySession session, IConfiguration config)
    {
        _session = session;
        _config = config;
    }

    private const int MaxItems = 50;

    public override void Configure()
    {
        Get("/api/public/{type}/feed.xml");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var type = Route<string>("type") ?? string.Empty;

        var def = await _session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);
        /* A feed is public delivery in another format, so it answers to the same opt-in. */
        if (!PublicDelivery.IsDeliverable(def)) { await SendNotFoundAsync(ct); return; }
        var slugField = PublicDelivery.SlugField(def!);

        var entries = await _session.Query<ContentDoc>()
            .Where(c => c.ContentType == type
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .OrderByDescending(c => c.CreatedAt)
            .Take(MaxItems)
            .ToListAsync(ct);

        var siteUrl = (_config["Feeds:SiteUrl"] ?? $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}").TrimEnd('/');
        var pathTemplate = _config[$"Feeds:Paths:{type}"] ?? $"/{type}/{{slug}}";
        var channelTitle = _config[$"Feeds:Titles:{type}"] ?? type;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<rss version=\"2.0\" xmlns:atom=\"http://www.w3.org/2005/Atom\">\n  <channel>\n");
        sb.Append($"    <title>{Esc(channelTitle)}</title>\n");
        sb.Append($"    <link>{Esc(siteUrl)}</link>\n");
        sb.Append($"    <description>{Esc(channelTitle)} — {Esc(type)} feed</description>\n");
        sb.Append($"    <atom:link href=\"{Esc(siteUrl)}/api/public/{Esc(type)}/feed.xml\" rel=\"self\" type=\"application/rss+xml\" />\n");

        foreach (var entry in entries)
        {
            var pub = PublicDelivery.ToPublic(entry, def, slugField);
            if (pub is null) continue; // fail-closed, same rules as the rest of delivery

            var slug = pub.Slug ?? string.Empty;
            var title = Field(pub.Data, "Title", "Name");
            var description = Field(pub.Data, "Excerpt", "Summary", "Description", "Body");
            var link = siteUrl + pathTemplate.Replace("{slug}", Uri.EscapeDataString(slug));
            var date = ItemDate(pub.Data, pub.CreatedAt);

            sb.Append("    <item>\n");
            if (title.Length > 0) sb.Append($"      <title>{Esc(title)}</title>\n");
            sb.Append($"      <link>{Esc(link)}</link>\n");
            sb.Append($"      <guid isPermaLink=\"false\">{Esc(pub.Id.ToString())}</guid>\n");
            sb.Append($"      <pubDate>{date.ToString("R", CultureInfo.InvariantCulture)}</pubDate>\n");
            if (description.Length > 0) sb.Append($"      <description><![CDATA[{description.Replace("]]>", "]]&gt;")}]]></description>\n");
            sb.Append("    </item>\n");
        }

        sb.Append("  </channel>\n</rss>\n");

        PublicDelivery.SetCache(HttpContext);
        await SendStringAsync(sb.ToString(), 200, "application/rss+xml; charset=utf-8", ct);
    }

    private static string Field(IReadOnlyDictionary<string, object> data, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = data.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase));
            if (hit.Value is not null)
            {
                var text = hit.Value.ToString();
                if (!string.IsNullOrWhiteSpace(text)) return text!;
            }
        }
        return string.Empty;
    }

    private static DateTimeOffset ItemDate(IReadOnlyDictionary<string, object> data, DateTimeOffset fallback)
    {
        var raw = Field(data, "Date", "PublishedAt");
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : fallback;
    }

    private static string Esc(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
