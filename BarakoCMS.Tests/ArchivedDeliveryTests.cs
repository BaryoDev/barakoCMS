using Xunit;
using FluentAssertions;
using System.Net;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using BarakoCMS.Tests.Builders;

namespace BarakoCMS.Tests;

/// <summary>
/// Archiving is how an entry retires (there is no content delete, see #102), so an Archived entry
/// must be as invisible to anonymous delivery as a Draft. Each route seeds a Published sibling of
/// the same type and asserts it IS returned, so none of these can pass against an empty table.
/// </summary>
[Collection("Sequential")]
public class ArchivedDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ArchivedDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // anonymous
    }

    private const string Needle = "quillonite";

    private async Task SeedAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeBuilder().Named(type).PubliclyDeliverable().WithTitleAndSlug().Build());

        Content Post(string slug, string title, ContentBuilder b)
        {
            var c = b.OfType(type).WithTitleAndSlug(title, slug).Build();
            c.SearchText = $"{title} {Needle}";
            return c;
        }

        s.Store(Post("live-post", "Live Post", new ContentBuilder().Published()));
        s.Store(Post("archived-post", "Archived Post", new ContentBuilder().Archived()));
        s.Store(Post("draft-post", "Draft Post", new ContentBuilder().Draft()));
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task An_archived_or_draft_entry_is_absent_from_the_public_list()
    {
        var type = $"archived_list_{Guid.NewGuid():N}";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("live-post", "the Published sibling proves the type is delivered");
        body.Should().NotContain("archived-post", "an Archived entry is retired from delivery");
        body.Should().NotContain("draft-post", "a Draft is never delivered");
    }

    [Fact]
    public async Task An_archived_or_draft_entry_is_absent_from_public_search()
    {
        var type = $"archived_search_{Guid.NewGuid():N}";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/search?q={Needle}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("live-post", "the Published sibling matches the query");
        body.Should().NotContain("archived-post", "an Archived entry is not searchable");
        body.Should().NotContain("draft-post", "a Draft is not searchable");
    }

    [Fact]
    public async Task An_archived_or_draft_entry_is_404_by_slug()
    {
        var type = $"archived_slug_{Guid.NewGuid():N}";
        await SeedAsync(type);

        (await _client.GetAsync($"/api/public/{type}/live-post")).StatusCode
            .Should().Be(HttpStatusCode.OK, "the Published sibling resolves by slug");
        (await _client.GetAsync($"/api/public/{type}/archived-post")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "an Archived entry is not addressable");
        (await _client.GetAsync($"/api/public/{type}/draft-post")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a Draft is not addressable");
    }

    [Fact]
    public async Task An_archived_or_draft_entry_is_absent_from_the_feed_and_sitemap()
    {
        var type = $"archived_feed_{Guid.NewGuid():N}";
        await SeedAsync(type);

        var feed = await (await _client.GetAsync($"/api/public/{type}/feed.xml")).Content.ReadAsStringAsync();
        feed.Should().Contain("Live Post");
        feed.Should().NotContain("Archived Post").And.NotContain("Draft Post");

        var sitemap = await (await _client.GetAsync("/api/public/sitemap.xml")).Content.ReadAsStringAsync();
        sitemap.Should().Contain($"/{type}/live-post");
        sitemap.Should().NotContain($"/{type}/archived-post").And.NotContain($"/{type}/draft-post");
    }
}
