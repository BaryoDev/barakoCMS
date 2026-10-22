using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// The feed and the sitemap link to the tenant's own site, the <c>Url</c> of its published
/// <c>site</c> entry, and fall back to <c>Feeds:SiteUrl</c> when it sets none (#923).
/// </summary>
[Collection("Sequential")]
public class TenantSiteOriginTests
{
    private const string Fallback = "https://test.example.com";
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public TenantSiteOriginTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A tenant with a deliverable <paramref name="type"/> holding one published entry, and a site entry when a URL is given.</summary>
    private async Task<string> TenantAsync(string type, string? siteUrl, ContentStatus siteStatus = ContentStatus.Published)
    {
        var slug = $"org-{Guid.NewGuid():N}"[..14];
        using (var scope = _fixture.Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            registry.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            await registry.SaveChangesAsync(Ct);
        }

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(slug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            RouteTemplate = "/news/{slug}",
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
            ],
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "Opening hours", ["Slug"] = "hours" },
        });
        if (siteUrl is not null)
        {
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = "site",
                Status = siteStatus,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Name"] = slug, ["Url"] = siteUrl },
            });
        }

        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<string> GetAsync(string tenant, string path)
    {
        var n = Interlocked.Increment(ref _ipCounter);
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.19.{n / 250 % 250}.{n % 250 + 1}");
        var response = await client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    [Fact]
    public async Task Two_tenants_feeds_and_sitemaps_link_to_their_own_sites()
    {
        var type = $"news{Guid.NewGuid():n}"[..12];
        var bakery = await TenantAsync(type, "https://bakery.example/");
        var shop = await TenantAsync(type, "https://shop.example");

        var bakeryFeed = await GetAsync(bakery, $"/api/public/{type}/feed.xml");
        var shopFeed = await GetAsync(shop, $"/api/public/{type}/feed.xml");

        bakeryFeed.Should().Contain("<link>https://bakery.example/news/hours</link>")
            .And.NotContain("shop.example").And.NotContain(Fallback);
        shopFeed.Should().Contain("<link>https://shop.example/news/hours</link>")
            .And.NotContain("bakery.example").And.NotContain(Fallback);

        var bakeryMap = await GetAsync(bakery, "/api/public/sitemap.xml");
        bakeryMap.Should().Contain("<loc>https://bakery.example/news/hours</loc>").And.NotContain(Fallback);
    }

    [Fact]
    public async Task The_feed_and_the_sitemap_carry_the_site_type_tag_so_a_site_change_purges_them()
    {
        var type = $"news{Guid.NewGuid():n}"[..12];
        var bakery = await TenantAsync(type, "https://bakery.example");
        var siteTag = $"t:{bakery}:type:site";

        foreach (var path in new[] { $"/api/public/{type}/feed.xml", "/api/public/sitemap.xml" })
        {
            var client = _fixture.CreateClient();
            var n = Interlocked.Increment(ref _ipCounter);
            client.DefaultRequestHeaders.Add("X-Tenant", bakery);
            client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.19.{n / 250 % 250}.{n % 250 + 1}");
            var response = await client.GetAsync(path, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var tags = response.Headers.GetValues("Cache-Tag").Single().Split(' ');
            tags.Should().NotBeEmpty();
            tags.Should().Contain(siteTag, $"{path} links to the site entry's Url");
        }
    }

    [Fact]
    public async Task A_tenant_without_a_usable_site_url_links_to_the_configured_fallback()
    {
        var type = $"news{Guid.NewGuid():n}"[..12];
        var none = await TenantAsync(type, siteUrl: null);
        var draft = await TenantAsync(type, "https://draft.example", ContentStatus.Draft);
        var scripted = await TenantAsync(type, "javascript:alert(1)");

        foreach (var tenant in new[] { none, draft, scripted })
        {
            var feed = await GetAsync(tenant, $"/api/public/{type}/feed.xml");
            feed.Should().Contain($"<link>{Fallback}/news/hours</link>", $"tenant {tenant} sets no usable site URL");
            feed.Should().NotContain("draft.example").And.NotContain("javascript:");
        }
    }

    [Fact]
    public async Task A_template_link_to_the_entry_uses_the_tenants_site_and_is_empty_for_a_draft()
    {
        var type = $"news{Guid.NewGuid():n}"[..12];
        var bakery = await TenantAsync(type, "https://bakery.example");
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(bakery);
        var published = await session.Query<Content>().SingleAsync(c => c.ContentType == type, Ct);
        var draft = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Draft,
            Data = new Dictionary<string, object> { ["Title"] = "Coming soon", ["Slug"] = "soon" },
        };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Feeds:SiteUrl"] = Fallback })
            .Build();
        const string template = "{{links.public}}";

        ITemplateVariableExtractor extractor = new TemplateVariableExtractor(session, null, null, config);
        await extractor.PrepareAsync(published, WorkflowEvents.Updated, 0, [template], Ct);
        extractor.ResolveVariables(template, published).Should().Be("https://bakery.example/news/hours");

        await extractor.PrepareAsync(draft, WorkflowEvents.Updated, 0, [template], Ct);
        extractor.ResolveVariables(template, draft).Should().BeEmpty("delivery does not serve a draft at its slug");
    }
}
