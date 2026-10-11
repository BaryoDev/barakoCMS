using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Markdig;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.EmailTemplates;

/// <summary>A template made ready to resolve: its subject, and its body as HTML inside its layout.</summary>
/// <param name="Subject">The subject as written, placeholders and all.</param>
/// <param name="Html">The body and the layout as HTML, with every placeholder where the author wrote it.</param>
/// <param name="Sources">The texts the author wrote, by field, for the placeholder warnings.</param>
internal sealed record RenderedTemplate(string Subject, string Html, IReadOnlyList<(string Field, string Text)> Sources);

/// <summary>
/// Email templates stored as content: finding one by id or slug, and turning its markdown and its
/// layout into the HTML a workflow resolves like an inline body.
/// </summary>
/// <remarks>
/// Shared by the Email action and the preview route, so a preview shows what a send would send.
/// Every read is through the caller's session, which is the tenant's, so a template or a layout of
/// another tenant is never found, by id or by slug.
/// </remarks>
internal static class EmailTemplateRenderer
{
    public const string TemplateType = "email-template";
    public const string LayoutType = "email-layout";
    public const string TemplateParameter = "Template";

    /// <summary>Past this a template name is not looked up. A slug or an id is far shorter.</summary>
    public const int MaxNameLength = 200;

    private static readonly TimeSpan ScanTimeout = TemplateExpression.ScanTimeout;

    /// <summary>Raw HTML off: a tag in the markdown is shown as text, so the only markup is what markdown and the layout make.</summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    private static readonly Regex UrlAttribute = new(
        @"\s(?<name>href|src)=""(?<value>[^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase, ScanTimeout);

    private static readonly Regex Colour = new(@"\A#(?:[0-9A-Fa-f]{3,4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\z", RegexOptions.Compiled);

    /// <summary>The name as a run record may quote it.</summary>
    public static string Shown(string name)
    {
        var text = name.Trim();
        return text.Length <= 80 ? text : text[..80] + "...";
    }

    /// <summary>
    /// The template an id or a slug names in this tenant, whatever its status, or null.
    /// </summary>
    /// <remarks>
    /// By slug it is the slug field and the case-insensitive match the authoring read by slug uses.
    /// Uniqueness is checked on write and is not a constraint, so among duplicates the oldest
    /// published one is taken, and the oldest of any status when none is published.
    /// </remarks>
    public static async Task<ContentDoc?> FindAsync(IQuerySession session, string? name, CancellationToken ct)
    {
        var text = name?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > MaxNameLength) return null;

        if (Guid.TryParse(text, out var id))
        {
            var byId = await session.LoadAsync<ContentDoc>(id, ct);
            return byId is not null && string.Equals(byId.ContentType, TemplateType, StringComparison.OrdinalIgnoreCase) ? byId : null;
        }

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == TemplateType, ct);
        var slugField = definition is null ? null : PublicDelivery.SlugFieldForAuthoring(definition);
        if (slugField is null) return null;

        var (sql, parameters) = DeliveryQuery.FieldEqualsIgnoreCaseSql(slugField, text);
        var matches = await session.Query<ContentDoc>()
            .Where(c => c.ContentType == TemplateType && c.MatchesSql(sql, parameters))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Take(10)
            .ToListAsync(ct);

        return matches.FirstOrDefault(m => m.Status == ContentStatus.Published) ?? matches.FirstOrDefault();
    }

    /// <summary>
    /// The published template a workflow names, made ready, or why it cannot be sent.
    /// </summary>
    /// <remarks>
    /// Every refusal here is permanent: a template that is missing, not published, or past the
    /// length cap is the same on the next try, until somebody changes it.
    /// </remarks>
    public static async Task<(RenderedTemplate? Rendered, string? Error)> ForSendingAsync(
        IQuerySession session, string name, CancellationToken ct)
    {
        var template = await FindAsync(session, name, ct);
        if (template is null)
        {
            return (null, $"No email template '{Shown(name)}' exists in this tenant.");
        }

        if (template.Status != ContentStatus.Published)
        {
            return (null, $"Email template '{Shown(name)}' is {template.Status}. Only a published template is sent.");
        }

        return await RenderAsync(session, template, ct);
    }

    /// <summary>
    /// A template's subject, and its body in its layout, or why it cannot be rendered. Its own
    /// status is not checked, so a draft can be previewed.
    /// </summary>
    /// <remarks>
    /// The layout it names has to be a published layout of this tenant. One that is missing or not
    /// published is refused rather than replaced by the default, which would change how the email
    /// looks without anybody being told. With no layout named, the tenant's published site entry
    /// gives the name, logo and colours.
    /// </remarks>
    /// <param name="session">The tenant's session.</param>
    /// <param name="template">The template, as the caller may read it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="shownLayout">
    /// For a caller who is a person: the layout as they may read it, or null when they may not read
    /// it. Without it the layout is used as stored, which is what a workflow run does.
    /// </param>
    public static async Task<(RenderedTemplate? Rendered, string? Error)> RenderAsync(
        IQuerySession session, ContentDoc template, CancellationToken ct,
        Func<ContentDoc, CancellationToken, Task<ContentDoc?>>? shownLayout = null)
    {
        var subject = Text(template, "Subject") ?? string.Empty;
        var body = Text(template, "Body") ?? string.Empty;

        if (string.IsNullOrWhiteSpace(subject)) return (null, "The email template has no subject.");
        if (string.IsNullOrWhiteSpace(body)) return (null, "The email template has no body.");

        Shell shell;
        string? header = null;
        string? footer = null;

        var layoutName = Text(template, "Layout");
        if (!string.IsNullOrWhiteSpace(layoutName))
        {
            var layout = Guid.TryParse(layoutName.Trim(), out var layoutId)
                ? await session.LoadAsync<ContentDoc>(layoutId, ct)
                : null;

            if (layout is null
                || !string.Equals(layout.ContentType, LayoutType, StringComparison.OrdinalIgnoreCase)
                || layout.Status != ContentStatus.Published)
            {
                return (null, "The layout the email template names is missing or not published.");
            }

            if (shownLayout is not null)
            {
                if (await shownLayout(layout, ct) is not { } shown)
                {
                    return (null, "The layout the email template names is not one you can read.");
                }

                layout = shown;
            }

            header = Text(layout, "Header");
            footer = Text(layout, "Footer");
            shell = new Shell(
                Text(layout, "Name"), Text(layout, "Logo"), Text(layout, "LogoAlt"),
                Text(layout, "Background"), Text(layout, "Text"), Text(layout, "Accent"), HeaderIsName: false, Copyright: null);
        }
        else
        {
            var site = await session.Query<ContentDoc>()
                .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                            && c.Status == ContentStatus.Published)
                .OrderBy(c => c.CreatedAt)
                .Take(1)
                .FirstOrDefaultAsync(ct);

            var colours = SiteColours(site);
            shell = new Shell(
                site is null ? null : Text(site, "Name"),
                site is null ? null : Text(site, "Logo"),
                site is null ? null : Text(site, "LogoAlt"),
                colours.GetValueOrDefault("pageBg"), colours.GetValueOrDefault("ink"), colours.GetValueOrDefault("accent"),
                HeaderIsName: true,
                Copyright: site is null ? null : Text(site, "Copyright"));
        }

        var sources = new List<(string Field, string Text)> { ("Subject", subject), ("Body", body) };
        if (!string.IsNullOrEmpty(header)) sources.Add(("Layout.Header", header));
        if (!string.IsNullOrEmpty(footer)) sources.Add(("Layout.Footer", footer));

        // The same cap a workflow parameter has, for the same reason: every run scans the text.
        foreach (var (field, text) in sources)
        {
            if (text.Length > TemplateExpression.MaxTemplateLength)
            {
                return (null, $"The email template's {field} is {text.Length} characters long, and it holds at most {TemplateExpression.MaxTemplateLength}.");
            }
        }

        var html = shell.Wrap(Markdown(header), Markdown(body), Markdown(footer));

        // The resolver leaves a text past the cap as written, so a body under the cap that grows
        // past it as HTML in its layout would go out with no placeholder filled. Refused here.
        if (html.Length > TemplateExpression.MaxTemplateLength)
        {
            return (null, $"The email template's body in its layout is {html.Length} characters long as HTML, and it holds at most {TemplateExpression.MaxTemplateLength}.");
        }

        return (new RenderedTemplate(subject, html, sources), null);
    }

    /// <summary>
    /// Markdown to HTML with every placeholder kept as written.
    /// </summary>
    /// <remarks>
    /// Each <c>{{...}}</c> is swapped for a run of letters and digits markdown leaves alone, and put
    /// back after, so a placeholder inside a link or next to an underscore reaches the resolver as
    /// the author wrote it. The resolve then runs once over the whole HTML, so a value is never read
    /// as markdown. A loop's markers are placeholders too, so a loop is best kept inside one
    /// paragraph or list item: across blocks its body repeats half of a block's tags.
    /// </remarks>
    internal static string Markdown(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var nonce = Guid.NewGuid().ToString("N");
        var holes = new List<string>();
        var shielded = TemplateExpression.Token.Replace(source, match =>
        {
            holes.Add(match.Value);
            return $"ph{nonce}x{holes.Count - 1}x";
        });

        var html = Markdig.Markdown.ToHtml(shielded, Pipeline);

        return Regex.Replace(
            html, $"ph{nonce}x(\\d+)x",
            match => int.TryParse(match.Groups[1].ValueSpan, out var index) && index < holes.Count ? holes[index] : match.Value,
            RegexOptions.None, ScanTimeout);
    }

    /// <summary>
    /// The subject and body a template sends for an entry: the subject resolved as an inline one
    /// is, the body with its values HTML-encoded, braces included, and then finished.
    /// </summary>
    /// <remarks>
    /// Braces are encoded in the body's values because <see cref="Finish"/> reads every
    /// <c>{{...}}</c> left in the HTML as the author's text. A value of <c>{{</c> in one field and
    /// <c>}}</c> in a later one would otherwise pair across the markup between them. Without an
    /// extractor the placeholders resolve as an unprepared template does.
    /// </remarks>
    public static (string Subject, string Body) Resolve(
        RenderedTemplate rendered, ContentDoc content, ITemplateVariableExtractor? extractor)
    {
        var subjectEncoding = barakoCMS.Features.Workflows.ActionParameters.EncodingFor("Email", "Subject");

        var subject = extractor is null
            ? TemplateVariableExtractor.Resolve(rendered.Subject, content, subjectEncoding)
            : extractor.ResolveVariables(rendered.Subject, content, subjectEncoding);
        var body = extractor is null
            ? TemplateVariableExtractor.Resolve(rendered.Html, content, TemplateValueEncoding.HtmlAndBraces)
            : extractor.ResolveVariables(rendered.Html, content, TemplateValueEncoding.HtmlAndBraces);

        return (subject, Finish(body));
    }

    /// <summary>
    /// What a resolved template body is sent as: a placeholder left as written cannot add markup,
    /// and a link or an image points only at http, https or (for a link) mailto.
    /// </summary>
    /// <remarks>
    /// A value is HTML-encoded with its braces when it is substituted (<see cref="Resolve"/>), so
    /// after the resolve every <c>{{...}}</c> is the author's own text from a placeholder the engine
    /// left as written. Its quotes and angle brackets are encoded here, which keeps a placeholder in an attribute inside
    /// that attribute. An attribute naming any other scheme, or no scheme, is dropped, so the link
    /// shows as its text.
    /// </remarks>
    public static string Finish(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;

        var holes = TemplateExpression.Token.Replace(html, match => match.Value
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal));

        return UrlAttribute.Replace(holes, match =>
            IsAllowedUrl(WebUtility.HtmlDecode(match.Groups["value"].Value), mailto: match.Groups["name"].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                ? match.Value
                : string.Empty);
    }

    private static bool IsAllowedUrl(string value, bool mailto)
    {
        var text = value.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        return uri.Scheme == Uri.UriSchemeHttp
            || uri.Scheme == Uri.UriSchemeHttps
            || (mailto && uri.Scheme == Uri.UriSchemeMailto);
    }

    /// <summary>The warnings a workflow gets for an inline body, for each text a template is made of.</summary>
    public static IEnumerable<(string Field, string Message)> Warnings(RenderedTemplate rendered, bool onTransition)
    {
        foreach (var (field, text) in rendered.Sources)
        {
            foreach (var problem in TemplateExpression.Problems(text, onTransition))
            {
                yield return (field, problem);
            }
        }
    }

    /// <summary>A field of an entry as text, matched in any case, or null when it is empty or absent.</summary>
    internal static string? Text(ContentDoc entry, string name)
    {
        if (entry.Data is null) return null;

        foreach (var (key, value) in entry.Data)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;

            var text = value switch
            {
                null => null,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
                JsonElement element => element.GetRawText(),
                _ => value.ToString(),
            };

            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }

    private static Dictionary<string, string> SiteColours(ContentDoc? site)
    {
        var colours = new Dictionary<string, string>(StringComparer.Ordinal);
        if (site?.Data is null) return colours;

        var raw = site.Data.FirstOrDefault(p => string.Equals(p.Key, "Colors", StringComparison.OrdinalIgnoreCase)).Value;
        try
        {
            using var doc = raw switch
            {
                JsonElement { ValueKind: JsonValueKind.Object } element => JsonDocument.Parse(element.GetRawText()),
                JsonElement { ValueKind: JsonValueKind.String } element => JsonDocument.Parse(element.GetString() ?? "{}"),
                string text => JsonDocument.Parse(text),
                null => null,
                _ => JsonDocument.Parse(JsonSerializer.Serialize(raw)),
            };

            if (doc?.RootElement.ValueKind != JsonValueKind.Object) return colours;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String) colours[property.Name] = property.Value.GetString()!;
            }
        }
        catch (JsonException)
        {
            // A site whose colours do not parse gets the default colours, not a failed email.
        }

        return colours;
    }

    /// <summary>The fixed HTML around a body. Every value in it is checked or encoded, and none can start a placeholder.</summary>
    private sealed record Shell(
        string? Name, string? Logo, string? LogoAlt, string? Background, string? Text, string? Accent, bool HeaderIsName, string? Copyright)
    {
        public string Wrap(string headerHtml, string bodyHtml, string footerHtml)
        {
            var background = ColourOr(Background, "#FFFFFF");
            var text = ColourOr(this.Text, "#1C1C1C");
            var accent = ColourOr(Accent, "#17458F");

            var html = new StringBuilder(bodyHtml.Length + headerHtml.Length + footerHtml.Length + 1024);
            html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\">")
                .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
                .Append("<style>a{color:").Append(accent).Append(";}</style></head>")
                .Append("<body style=\"margin:0;padding:0;background:").Append(background).Append(";color:").Append(text)
                .Append(";font-family:Arial,Helvetica,sans-serif;\">")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:").Append(background).Append(";\">")
                .Append("<tr><td align=\"center\" style=\"padding:24px 16px;\">")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;\">");

            if (AbsoluteHttp(Logo) is { } logo)
            {
                html.Append("<tr><td style=\"padding:0 0 16px 0;\"><img src=\"").Append(Literal(logo))
                    .Append("\" alt=\"").Append(Literal(LogoAlt ?? Name ?? string.Empty))
                    .Append("\" style=\"max-width:200px;height:auto;border:0;\"></td></tr>");
            }

            var header = HeaderIsName
                ? (string.IsNullOrWhiteSpace(Name) ? string.Empty : $"<p><strong>{Literal(Name)}</strong></p>")
                : headerHtml;
            if (header.Length > 0) html.Append("<tr><td style=\"padding:0 0 16px 0;\">").Append(header).Append("</td></tr>");

            html.Append("<tr><td style=\"line-height:1.5;\">").Append(bodyHtml).Append("</td></tr>");

            var footer = HeaderIsName
                ? (string.IsNullOrWhiteSpace(Copyright) ? string.Empty : $"<p>{Literal(Copyright)}</p>")
                : footerHtml;
            if (footer.Length > 0) html.Append("<tr><td style=\"padding:24px 0 0 0;font-size:12px;\">").Append(footer).Append("</td></tr>");

            html.Append("</table></td></tr></table></body></html>");
            return html.ToString();
        }

        private string ColourOr(string? value, string fallback) =>
            value is not null && Colour.IsMatch(value.Trim()) ? value.Trim() : fallback;

        private static string? AbsoluteHttp(string? value) =>
            value is not null
            && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value.Trim()
                : null;

        /// <summary>Encoded, with its braces too, so a setting can never be read as a placeholder.</summary>
        private static string Literal(string value) =>
            WebUtility.HtmlEncode(value).Replace("{", "&#123;", StringComparison.Ordinal).Replace("}", "&#125;", StringComparison.Ordinal);
    }
}
