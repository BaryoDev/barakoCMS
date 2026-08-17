using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using BarakoCMS.Tests.Builders;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Written during review of the sitemap endpoint, to prove the claims in that review rather than
/// assert them. Each test here fails against the endpoint as submitted.
/// </summary>
[Collection("Sequential")]
public class SitemapReviewTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public SitemapReviewTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The fixture sets no Feeds:SiteUrl. The RSS feed handles that by falling back to the request
    /// host; the sitemap returns 500. A sitemap that errors on a fresh install is worse than one
    /// that infers the host, and the two endpoints should not disagree.
    /// </summary>
    [Fact]
    public async Task Sitemap_WithNoConfiguredSiteUrl_FallsBackToTheRequestHost()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = $"smrev_fallback_{Guid.NewGuid():N}"[..24];

        session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());
        session.Store(new ContentBuilder().OfType(type).WithTitleAndSlug("Post", "fallback-post").Published().Build());
        await session.SaveChangesAsync();

        var res = await _client.GetAsync("/api/public/sitemap.xml");

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "the RSS feed falls back to the request host when Feeds:SiteUrl is unset, and these two should agree");
        (await res.Content.ReadAsStringAsync()).Should().Contain("fallback-post");
    }

    /// <summary>
    /// lastmod means last modification. Using the creation date means an edited page never signals a
    /// re-crawl, which is the one job the field has.
    /// </summary>
    [Fact]
    public async Task Sitemap_LastMod_ReflectsTheLastUpdateNotTheCreation()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = $"smrev_lastmod_{Guid.NewGuid():N}"[..24];

        session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());

        var entry = new ContentBuilder()
            .OfType(type)
            .WithTitleAndSlug("Edited", "edited-post")
            .CreatedAt(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .Published()
            .Build();
        entry.UpdatedAt = new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        session.Store(entry);
        await session.SaveChangesAsync();

        var xml = await (await _client.GetAsync("/api/public/sitemap.xml")).Content.ReadAsStringAsync();

        xml.Should().Contain("2026-06-15", "lastmod should be the date the entry was last modified");
        xml.Should().NotContain("2020-01-01", "the creation date is not a modification date");
    }

    /// <summary>
    /// A content type with no slug field yields Slug == null for every entry, which the endpoint
    /// turns into an empty string. Every entry of that type then produces the identical URL, so the
    /// sitemap advertises one page many times.
    /// </summary>
    [Fact]
    public async Task Sitemap_EntriesWithNoSlug_DoNotProduceDuplicateUrls()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = $"smrev_noslug_{Guid.NewGuid():N}"[..24];

        // Title only: no slug field on the type, so nothing is slug-addressable.
        session.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithField("Title").Build());
        foreach (var t in new[] { "One", "Two", "Three" })
            session.Store(new ContentBuilder().OfType(type).With("Title", t).Published().Build());
        await session.SaveChangesAsync();

        var xml = await (await _client.GetAsync("/api/public/sitemap.xml")).Content.ReadAsStringAsync();

        var locs = System.Text.RegularExpressions.Regex.Matches(xml, "<loc>(.*?)</loc>")
            .Select(m => m.Groups[1].Value)
            .Where(u => u.Contains(type))
            .ToList();

        // Guard the guard: an empty match list would make the uniqueness assertion pass vacuously,
        // which would prove nothing at all.
        locs.Should().HaveCount(3, "all three entries should appear in the sitemap");
        locs.Should().OnlyHaveUniqueItems(
            "three entries with no slug must not all advertise the same URL");
    }
}
