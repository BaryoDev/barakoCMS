using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.ContentType.Blueprints;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// The feed, the sitemap and the SEO block read a type's field roles and route template, over an
/// anonymous client, and a type that declares neither is served exactly as before.
/// </summary>
/// <remarks>
/// Each role case stores two types holding the same values: one with the field names the readers
/// guessed before roles existed, one with other names and the roles declared. The second is where
/// a reader that ignores roles shows: its items have no title, no description and the wrong date.
///
/// Every type and slug is unique to its test, and an item is found by its own slug, because the
/// sitemap lists every deliverable type the shared database holds.
/// </remarks>
[Collection("Sequential")]
public class FieldRolesPublicTests
{
    private const string Site = "https://test.example.com";

    private static readonly string Suffix = Guid.NewGuid().ToString("n")[..10];
    private static readonly string RoutedType = $"routed-{Suffix}";
    private static readonly string ConfiguredType = $"configured-{Suffix}";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _configured;

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FieldRolesPublicTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // One derived host for the class, never disposed: it shares the fixture's server.
    private WebApplicationFactory<Program> HostWithConfiguredPaths()
    {
        lock (Gate)
        {
            return _configured ??= _factory.WithSettings(new Dictionary<string, string?>
            {
                { $"Feeds:Paths:{RoutedType}", "/from-config/{slug}" },
                { $"Feeds:Paths:{ConfiguredType}", "/from-config/{slug}" },
            });
        }
    }

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():n}"[..(prefix.Length + 11)];

    private static FieldDefinition Field(
        string name, string type = "string", string? role = null,
        SensitivityLevel sensitivity = SensitivityLevel.Public) => new()
    {
        Name = name, DisplayName = name, Type = type, Role = role, Sensitivity = sensitivity,
    };

    private async Task StoreAsync(ContentTypeDefinition type, params Content[] entries)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(type);
        foreach (var entry in entries)
            session.Store(entry);
        await session.SaveChangesAsync(Ct);
    }

    private static ContentTypeDefinition Deliverable(string name, params FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name, IsPubliclyDeliverable = true, Fields = fields.ToList(),
    };

    private static Content Published(string type, DateTime createdAt, params (string Field, object Value)[] data) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
        Data = data.ToDictionary(d => d.Field, d => d.Value),
    };

    private sealed record FeedItem(string? Title, string Link, string PubDate, string? Description);

    private static async Task<List<FeedItem>> FeedAsync(HttpClient client, string type)
    {
        var response = await client.GetAsync($"/api/public/{type}/feed.xml", Ct);
        var xml = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);

        return XDocument.Parse(xml).Descendants("item")
            .Select(item => new FeedItem(
                item.Element("title")?.Value,
                item.Element("link")!.Value,
                item.Element("pubDate")!.Value,
                item.Element("description")?.Value))
            .ToList();
    }

    private static async Task<string> SitemapAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/public/sitemap.xml", Ct);
        var xml = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return xml;
    }

    [Fact]
    public async Task A_type_with_renamed_fields_and_roles_produces_the_feed_and_sitemap_the_name_based_type_does()
    {
        var byName = NewName("byname");
        var byRole = NewName("byrole");
        var created = new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);
        const string starts = "2026-03-02T09:30:00Z";

        await StoreAsync(
            Deliverable(byName,
                Field("Title"), Field("Slug", "slug"), Field("Excerpt", "text"), Field("PublishedAt", "datetime")),
            Published(byName, created,
                ("Title", "Harvest Fair"), ("Slug", "harvest-fair"),
                ("Excerpt", "Stalls & music"), ("PublishedAt", starts)));

        await StoreAsync(
            Deliverable(byRole,
                Field("EventName", role: "title"), Field("Slug", "slug"),
                Field("Teaser", "text", role: "summary"), Field("StartsOn", "datetime", role: "date")),
            Published(byRole, created,
                ("EventName", "Harvest Fair"), ("Slug", "harvest-fair"),
                ("Teaser", "Stalls & music"), ("StartsOn", starts)));

        var named = await FeedAsync(_client, byName);
        var roled = await FeedAsync(_client, byRole);

        named.Should().HaveCount(1);
        roled.Should().HaveCount(1);

        named[0].Should().Be(new FeedItem(
            "Harvest Fair", $"{Site}/{byName}/harvest-fair", "Mon, 02 Mar 2026 09:30:00 GMT", "Stalls & music"));
        roled[0].Should().Be(named[0] with { Link = $"{Site}/{byRole}/harvest-fair" });

        var sitemap = await SitemapAsync(_client);
        foreach (var type in new[] { byName, byRole })
        {
            sitemap.Should().Contain(
                $"  <url>\n    <loc>{Site}/{type}/harvest-fair</loc>\n    <lastmod>2026-01-10</lastmod>\n  </url>\n");
        }
    }

    [Fact]
    public async Task The_field_holding_a_role_is_read_ahead_of_a_guessed_name_and_the_name_fills_in_when_it_is_empty()
    {
        var type = NewName("ahead");
        var created = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        await StoreAsync(
            Deliverable(type,
                Field("Name"), Field("Headline", role: "title"), Field("Slug", "slug"),
                Field("Body", "markdown"), Field("Intro", "text", role: "summary")),
            Published(type, created,
                ("Name", "internal-name-a"), ("Headline", "Open day"), ("Slug", "both-set"),
                ("Body", "The long body"), ("Intro", "Come and see")),
            Published(type, created.AddDays(1),
                ("Name", "internal-name-b"), ("Headline", ""), ("Slug", "role-empty"),
                ("Body", "Only a body")));

        var items = await FeedAsync(_client, type);

        items.Should().HaveCount(2);
        var bothSet = items.Single(i => i.Link.EndsWith("/both-set", StringComparison.Ordinal));
        var roleEmpty = items.Single(i => i.Link.EndsWith("/role-empty", StringComparison.Ordinal));

        bothSet.Title.Should().Be("Open day");
        bothSet.Description.Should().Be("Come and see");
        roleEmpty.Title.Should().Be("internal-name-b");
        roleEmpty.Description.Should().Be("Only a body");
    }

    [Fact]
    public async Task A_role_on_a_field_that_is_not_public_puts_nothing_of_it_in_the_feed()
    {
        var type = NewName("hidden");

        await StoreAsync(
            Deliverable(type,
                Field("InternalTitle", role: "title", sensitivity: SensitivityLevel.Sensitive),
                Field("Slug", "slug"),
                Field("Notes", "text", role: "summary", sensitivity: SensitivityLevel.Hidden)),
            Published(type, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                ("InternalTitle", "codename-osprey"), ("Slug", "public-slug"), ("Notes", "do-not-publish")));

        var response = await _client.GetAsync($"/api/public/{type}/feed.xml", Ct);
        var xml = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        xml.Should().Contain("/public-slug", "the entry itself is in the feed");
        xml.Should().NotContain("codename-osprey").And.NotContain("do-not-publish");
    }

    [Fact]
    public async Task A_route_template_on_the_type_is_read_by_the_feed_and_the_sitemap_ahead_of_the_configured_path()
    {
        var created = new DateTime(2026, 4, 4, 0, 0, 0, DateTimeKind.Utc);
        var routedSlug = $"routed-entry-{Suffix}";
        var configuredSlug = $"configured-entry-{Suffix}";

        var routed = Deliverable(RoutedType, Field("Title"), Field("Slug", "slug"));
        routed.RouteTemplate = "/whats-on/{slug}";

        await StoreAsync(routed, Published(RoutedType, created, ("Title", "Routed"), ("Slug", routedSlug)));
        await StoreAsync(
            Deliverable(ConfiguredType, Field("Title"), Field("Slug", "slug")),
            Published(ConfiguredType, created, ("Title", "Configured"), ("Slug", configuredSlug)));

        var client = HostWithConfiguredPaths().CreateClient();

        var routedFeed = await FeedAsync(client, RoutedType);
        var configuredFeed = await FeedAsync(client, ConfiguredType);

        routedFeed.Should().HaveCount(1);
        routedFeed[0].Link.Should().Be($"{Site}/whats-on/{routedSlug}");
        configuredFeed.Should().HaveCount(1);
        configuredFeed[0].Link.Should().Be($"{Site}/from-config/{configuredSlug}",
            "the control: a type with no template keeps the configured path");

        var sitemap = await SitemapAsync(client);
        sitemap.Should().Contain($"<loc>{Site}/whats-on/{routedSlug}</loc>");
        sitemap.Should().NotContain($"/from-config/{routedSlug}");
        sitemap.Should().Contain($"<loc>{Site}/from-config/{configuredSlug}</loc>");
    }

    [Fact]
    public async Task The_seo_title_of_a_delivered_entry_falls_back_to_the_field_holding_the_title_role()
    {
        var type = NewName("seo");

        var definition = Deliverable(type, Field("Name"), Field("Headline", role: "title"), Field("Slug", "slug"));
        definition.Fields.AddRange(barakoCMS.Features.Seo.SeoFields.Definitions());

        await StoreAsync(
            definition,
            Published(type, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                ("Name", "internal-name"), ("Headline", "Open day at the school"), ("Slug", "open-day")));

        var response = await _client.GetAsync($"/api/public/{type}/open-day", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("seo").GetProperty("title").GetString().Should().Be("Open day at the school");
    }

    /// <summary>
    /// The devsite blueprint declares no role and no route template, so its feed and sitemap have
    /// to read exactly as they did. The expected text is what the name-based rules produce, written
    /// out, so this passes before the change and after it.
    /// </summary>
    [Fact]
    public async Task The_devsite_blueprints_post_type_keeps_the_feed_item_and_sitemap_entry_it_had()
    {
        ContentTypeDefinition post;
        using (var scope = _factory.Services.CreateScope())
        {
            var devsite = scope.ServiceProvider.GetRequiredService<BlueprintCatalog>().Find("devsite");
            devsite.Should().NotBeNull();
            devsite!.Definition.Should().NotBeNull();
            post = BlueprintCatalog.Materialize(devsite.Definition!).Single(t => t.Name == "post");
        }

        post.Fields.Should().NotBeEmpty();
        post.Fields.Should().OnlyContain(f => f.Editor == null && f.Section == null && f.Role == null);
        post.RouteTemplate.Should().BeNull();

        var type = NewName("devpost");
        post.Name = type;

        var entry = Published(type, new DateTime(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc),
            ("Title", "Hello & welcome"), ("Slug", "hello-welcome"),
            ("Excerpt", "A <b>short</b> excerpt"), ("Body", "The body, which the excerpt goes ahead of"),
            ("PublishedAt", "2026-03-02T09:30:00Z"));
        entry.UpdatedAt = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc);

        await StoreAsync(post, entry);

        var response = await _client.GetAsync($"/api/public/{type}/feed.xml", Ct);
        var xml = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, xml);

        xml.Should().Contain(
            "    <item>\n"
            + "      <title>Hello &amp; welcome</title>\n"
            + $"      <link>{Site}/{type}/hello-welcome</link>\n"
            + $"      <guid isPermaLink=\"false\">{entry.Id}</guid>\n"
            + "      <pubDate>Mon, 02 Mar 2026 09:30:00 GMT</pubDate>\n"
            + "      <description>A &lt;b&gt;short&lt;/b&gt; excerpt</description>\n"
            + "    </item>\n");

        (await SitemapAsync(_client)).Should().Contain(
            $"  <url>\n    <loc>{Site}/{type}/hello-welcome</loc>\n    <lastmod>2026-03-05</lastmod>\n  </url>\n");
    }
}
