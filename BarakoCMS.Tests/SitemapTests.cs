using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.Tests.Builders;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class SitemapTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public SitemapTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Sitemap_ContainsOnlyPublishedPublicEntriesOfDeliverableTypes()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        const string publicType = "sitemap_public";
        const string privateType = "sitemap_private";

        session.Store(
            new ContentTypeBuilder()
                .Named(publicType)
                .PubliclyDeliverable()
                .WithTitleAndSlug()
                .Build());

        session.Store(
            new ContentTypeBuilder()
                .Named(privateType)
                .WithTitleAndSlug()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(publicType)
                .WithTitleAndSlug("Public Post", "public-post")
                .Published()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(privateType)
                .WithTitleAndSlug("Private Post", "private-post")
                .Published()
                .Build());

        await session.SaveChangesAsync();

        var response = await _client.GetAsync("/api/public/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();

        xml.Should().Contain("public-post");
        xml.Should().NotContain("private-post");
    }

    [Fact]
    public async Task Sitemap_ContainsLastModifiedDate()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        // Unique per test: the content-type name carries a unique index now, so two tests sharing
        // one literal collide on the second insert.
        var type = $"sitemap_lastmod_{Guid.NewGuid():N}";
        var createdAt = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

        session.Store(
            new ContentTypeBuilder()
                .Named(type)
                .PubliclyDeliverable()
                .WithTitleAndSlug()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Last Modified Post", "last-modified")
                .CreatedAt(createdAt)
                .Published()
                .Build());

        await session.SaveChangesAsync();

        var response = await _client.GetAsync("/api/public/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();

        xml.Should().Contain("<lastmod>2026-03-15</lastmod>");
    }

    [Fact]
    public async Task Sitemap_UsesConfiguredFrontendPath()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        const string type = "sitemap_paths";

        session.Store(
            new ContentTypeBuilder()
                .Named(type)
                .PubliclyDeliverable()
                .WithTitleAndSlug()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Configured Path Post", "hello-world")
                .Published()
                .Build());

        await session.SaveChangesAsync();

        var response = await _client.GetAsync("/api/public/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();

        xml.Should().Contain("/articles/hello-world");
    }

    [Fact]
    public async Task Sitemap_ExcludesDraftAndSensitiveEntries()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        const string type = "sitemap_filtering";

        session.Store(
            new ContentTypeBuilder()
                .Named(type)
                .PubliclyDeliverable()
                .WithTitleAndSlug()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Published Post", "published")
                .Published()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Draft Post", "draft")
                .Draft()
                .Build());

        session.Store(
            new ContentBuilder()
                .OfType(type)
                .WithTitleAndSlug("Sensitive Post", "sensitive")
                .Published()
                .Sensitive()
                .Build());

        await session.SaveChangesAsync();

        var response = await _client.GetAsync("/api/public/sitemap.xml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var xml = await response.Content.ReadAsStringAsync();

        xml.Should().Contain("/sitemap_filtering/published");
        xml.Should().NotContain("/sitemap_filtering/draft");
        xml.Should().NotContain("/sitemap_filtering/sensitive");
    }

    [Fact]
    public async Task Sitemap_LastMod_UsesUpdatedAt()
    {
        var type = $"sitemap_lastmod_{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeBuilder()
            .Named(type)
            .PubliclyDeliverable()
            .WithTitleAndSlug()
            .Build());

        s.Store(new ContentBuilder()
            .OfType(type)
            .WithTitleAndSlug("Updated Post", "updated-post")
            .CreatedAt(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .UpdatedAt(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc))
            .Published()
            .Build());

        await s.SaveChangesAsync();

        var response = await _client.GetAsync("/api/public/sitemap.xml");
        var xml = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        xml.Should().Contain("<lastmod>2026-06-15</lastmod>");
        xml.Should().NotContain("<lastmod>2020-01-01</lastmod>");
    }
}
