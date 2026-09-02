using Xunit;
using FluentAssertions;
using System.Net;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.Builders;

namespace BarakoCMS.Tests;

/// <summary>
/// RSS feed (GET /api/public/{type}/feed.xml) over an ANONYMOUS client. Adversarial: only Published,
/// document-Public entries appear, only over Public fields — a draft, a Sensitive document, and a value
/// in a Sensitive field must never surface. Newest first; item links use the frontend path template.
/// </summary>
[Collection("Sequential")]
public class FeedTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FeedTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeBuilder()
            .Named(type)
            .PubliclyDeliverable()
            .WithTitleAndSlug()
            .WithField("Excerpt")
            .WithSensitiveField()
            .Build());

        ContentBuilder Post(string slug, string title, DateTime created) => new ContentBuilder()
            .OfType(type)
            .WithTitleAndSlug(title, slug)
            .With("Excerpt", $"excerpt of {slug}")
            .With("Secret", "topsecret")
            .CreatedAt(created);

        var jan = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        s.Store(Post("older", "Older Post", jan).Published().Build());
        s.Store(Post("newer", "Newer Post", jan.AddMonths(1)).Published().Build());
        s.Store(Post("wip", "Draft Post", jan.AddMonths(2)).Build());          // a draft is never delivered
        s.Store(Post("hidden", "Hidden Post", jan.AddMonths(2).AddDays(1)).Sensitive().Published().Build());
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Feed_IsRss_WithOnlyPublishedPublicItems()
    {
        var type = "feed_a"; await SeedAsync(type);
        var res = await _client.GetAsync($"/api/public/{type}/feed.xml");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/rss+xml");
        var xml = await res.Content.ReadAsStringAsync();

        xml.Should().StartWith("<?xml").And.Contain("<rss").And.Contain("<channel>");
        xml.Should().Contain("Older Post").And.Contain("Newer Post");
        xml.Should().NotContain("Draft Post", "drafts are never in the feed");
        xml.Should().NotContain("Hidden Post", "a document-Sensitive entry never appears");
        xml.Should().NotContain("topsecret", "a Sensitive field value is never emitted");
    }

    [Fact]
    public async Task Feed_ItemsAreNewestFirst()
    {
        var type = "feed_b"; await SeedAsync(type);
        var xml = await (await _client.GetAsync($"/api/public/{type}/feed.xml")).Content.ReadAsStringAsync();
        xml.IndexOf("Newer Post", StringComparison.Ordinal)
            .Should().BeLessThan(xml.IndexOf("Older Post", StringComparison.Ordinal), "newest entry comes first");
    }

    [Fact]
    public async Task Feed_ItemLinkUsesTypeSlugPathByDefault()
    {
        var type = "feed_c"; await SeedAsync(type);
        var xml = await (await _client.GetAsync($"/api/public/{type}/feed.xml")).Content.ReadAsStringAsync();
        // The fixture sets Feeds:SiteUrl; with no Feeds:Paths:{type} the default path is /{type}/{slug}.
        xml.Should().Contain($"/{type}/newer").And.Contain($"/{type}/older");
    }

    /// <summary>
    /// The links in a feed are read by crawlers and aggregators, so they must not come from a header
    /// the caller wrote. With nothing configured and AllowedHosts accepting every host, there is no
    /// trustworthy origin to build them from and the feed refuses rather than guessing (#147).
    /// </summary>
    /// <remarks>
    /// A two-label host on purpose. Tenant resolution reads the leading subdomain, so a three-label
    /// forgery would move the request to a tenant that holds none of this test's content and the 404
    /// would hide what is being asserted.
    /// </remarks>
    private static HttpRequestMessage ForgedHost(string type) =>
        new(HttpMethod.Get, $"/api/public/{type}/feed.xml")
        {
            Headers = { Host = "attacker-example.net" },
        };

    [Fact]
    public async Task A_forged_host_never_becomes_the_link_origin()
    {
        var type = "feed_forged"; await SeedAsync(type);

        // Feeds:SiteUrl removed, App:BaseUrl was never set, and AllowedHosts is "*": the shipped
        // defaults, which is the configuration this was exploitable under.
        var client = _factory.WithSetting("Feeds:SiteUrl", null).CreateClient();

        var res = await client.SendAsync(ForgedHost(type));
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().NotContain("attacker-example.net", "the caller does not get to choose the origin");
        body.Should().Contain("Feeds:SiteUrl", "the refusal names the setting that fixes it");
    }

    /// <summary>
    /// The positive control. A feed that refused every request would pass the test above, and the
    /// configured deployment is the one that has to keep working.
    /// </summary>
    [Fact]
    public async Task A_configured_site_url_serves_the_feed_and_survives_a_forged_host()
    {
        var type = "feed_configured"; await SeedAsync(type);

        var res = await _client.SendAsync(ForgedHost(type));
        var body = await res.Content.ReadAsStringAsync();

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("https://test.example.com", "the fixture configures Feeds:SiteUrl");
        body.Should().NotContain("attacker-example.net");
    }

    [Fact]
    public async Task An_unknown_type_returns_404_for_rss()
    {
        // This used to answer 200 with an empty but valid feed. It now refuses, because delivery is
        // opt-in and an unknown type and an un-opted-in one must be indistinguishable — answering
        // differently would confirm which types exist.
        var res = await _client.GetAsync("/api/public/nosuchtype/feed.xml");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
