using System.Text;
using FastEndpoints;
using Marten;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public;

public class SitemapEndpoint : EndpointWithoutRequest
{
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;

    public SitemapEndpoint(IQuerySession session, IConfiguration config)
    {
        _session = session;
        _config = config;
    }

    public override void Configure()
    {
        Get("/api/public/sitemap.xml");
        AllowAnonymous();
    }
    public override async Task HandleAsync(CancellationToken ct)
    {
        var definitions = await _session.Query<ContentTypeDefinition>()
            .ToListAsync(ct);

        var deliverableDefinitions = definitions
            .Where(PublicDelivery.IsDeliverable)
            .ToList();

        var deliverableTypes = deliverableDefinitions
            .Select(d => d.Name)
            .ToList();

        var entries = await _session.Query<ContentDoc>()
            .Where(c => deliverableTypes.Contains(c.ContentType)
                        && c.Status == ContentStatus.Published
                        && c.Sensitivity == SensitivityLevel.Public)
            .Take(50000)
            .ToListAsync(ct);

        var siteUrl = _config["Feeds:SiteUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(siteUrl)
            || !Uri.TryCreate(siteUrl, UriKind.Absolute, out _))
        {
            await SendErrorsAsync(500, ct);
            return;
        }

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

        foreach (var def in definitions)
        {
            if (!PublicDelivery.IsDeliverable(def))
                continue;

            var type = def.Name;
            var slugField = PublicDelivery.SlugField(def);

            var typeEntries = entries
                .Where(c => c.ContentType == type);


            var pathTemplate = _config[$"Feeds:Paths:{type}"]
                            ?? $"/{type}/{{slug}}";

            foreach (var entry in typeEntries)
            {
                var pub = PublicDelivery.ToPublic(entry, def, slugField);
                if (pub is null)
                    continue;

                if (string.IsNullOrWhiteSpace(pub.Slug))
                    continue;

                var slug = pub.Slug;
                var link = siteUrl + pathTemplate.Replace(
                    "{slug}",
                    Uri.EscapeDataString(slug));

                sb.Append("  <url>\n");
                sb.Append($"    <loc>{Esc(link)}</loc>\n");
                sb.Append($"    <lastmod>{pub.UpdatedAt:yyyy-MM-dd}</lastmod>\n");
                sb.Append("  </url>\n");
            }
        }

        sb.Append("</urlset>\n");

        PublicDelivery.SetCache(HttpContext);
        await SendStringAsync(
            sb.ToString(),
            200,
            "application/xml; charset=utf-8",
            ct);
    }
    private static string Esc(string value) =>
    value.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");
}
