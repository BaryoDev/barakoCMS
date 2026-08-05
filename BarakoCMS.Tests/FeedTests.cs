using Xunit;
using FluentAssertions;
using System.Net;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

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
        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Excerpt", DisplayName = "Excerpt", Type = "string" },
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });
        void Doc(string slug, string title, ContentStatus st, SensitivityLevel sev, DateTime created) =>
            s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = st, Sensitivity = sev, CreatedAt = created,
                Data = new() { ["Title"] = title, ["Slug"] = slug, ["Excerpt"] = $"excerpt of {slug}", ["Secret"] = "topsecret" } });

        Doc("older", "Older Post", ContentStatus.Published, SensitivityLevel.Public, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Doc("newer", "Newer Post", ContentStatus.Published, SensitivityLevel.Public, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Doc("wip", "Draft Post", ContentStatus.Draft, SensitivityLevel.Public, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Doc("hidden", "Hidden Post", ContentStatus.Published, SensitivityLevel.Sensitive, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));
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
        // With no Feeds:SiteUrl config, the site URL falls back to the request host; the default path is /{type}/{slug}.
        xml.Should().Contain($"/{type}/newer").And.Contain($"/{type}/older");
    }

    [Fact]
    public async Task Feed_UnknownType_IsEmptyButValidRss()
    {
        var res = await _client.GetAsync("/api/public/nosuchtype/feed.xml");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var xml = await res.Content.ReadAsStringAsync();
        xml.Should().Contain("<rss").And.Contain("</rss>");
        xml.Should().NotContain("<item>");
    }
}
