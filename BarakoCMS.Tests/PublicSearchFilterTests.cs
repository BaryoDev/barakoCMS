using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>GET /api/public/{type}/search</c> takes the list's <c>filter[field][op]=value</c> parameters,
/// applies them before the limit, and refuses one on a field that is not Public.
/// </summary>
[Collection("Sequential")]
public class PublicSearchFilterTests
{
    private const string Needle = "quillonite";

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // anonymous

    public PublicSearchFilterTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Three sports entries with the needle in the title, which ranks above a body hit, and one
    // news entry with it in the body. A limit of two therefore never reaches the news entry
    // unless the filter runs before the limit.
    private async Task<string> SeedAsync()
    {
        var type = "sf" + Guid.NewGuid().ToString("N")[..10];
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = new()
            {
                new() { Name = "Title", DisplayName = "Title", Type = "string" },
                new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new() { Name = "Body", DisplayName = "Body", Type = "markdown" },
                new() { Name = "Category", DisplayName = "Category", Type = "string" },
                new()
                {
                    Name = "Secret", DisplayName = "Secret", Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive,
                },
            },
        });

        void Add(string slug, string title, string body, string category, ContentStatus status = ContentStatus.Published) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                Sensitivity = SensitivityLevel.Public,
                Data = new()
                {
                    ["Title"] = title, ["Slug"] = slug, ["Body"] = body, ["Category"] = category,
                    ["Secret"] = "classified",
                },
                SearchText = $"{title} {slug} {body} {category}",
            });

        Add("sports-1", $"{Needle} one", "plain", "sports");
        Add("sports-2", $"{Needle} two", "plain", "sports");
        Add("sports-3", $"{Needle} three", "plain", "sports");
        Add("news-1", "Nothing special", $"a paragraph mentioning {Needle} once", "news");
        Add("news-draft", $"{Needle} draft", "plain", "news", ContentStatus.Draft);

        await s.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private async Task<(List<string> Slugs, int Count)> SearchAsync(string type, string query)
    {
        var response = await _client.GetAsync(
            $"/api/public/{type}/search?q={Needle}&{query}", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        return (
            root.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("slug").GetString()!).ToList(),
            root.GetProperty("count").GetInt32());
    }

    private async Task<HttpStatusCode> StatusAsync(string type, string query) =>
        (await _client.GetAsync($"/api/public/{type}/search?{query}", TestContext.Current.CancellationToken)).StatusCode;

    [Fact]
    public async Task A_filter_is_applied_before_the_limit()
    {
        var type = await SeedAsync();

        var unfiltered = await SearchAsync(type, "limit=2");
        unfiltered.Slugs.Should().HaveCount(2);
        unfiltered.Slugs.Should().NotContain("news-1",
            "two title hits fill the limit, so the news entry is only reachable through the filter");

        var news = await SearchAsync(type, "limit=2&filter[Category][eq]=news");
        news.Slugs.Should().HaveCount(1);
        news.Slugs.Should().Equal("news-1");
        news.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_filter_does_not_surface_a_draft_that_matches_it()
    {
        var type = await SeedAsync();

        var news = await SearchAsync(type, "limit=50&filter[Category][eq]=news");

        news.Slugs.Should().HaveCount(1);
        news.Slugs.Should().NotContain("news-draft");
    }

    [Fact]
    public async Task A_filter_on_a_field_that_is_not_public_is_refused()
    {
        var type = await SeedAsync();

        (await StatusAsync(type, $"q={Needle}&filter[Secret][eq]=classified"))
            .Should().Be(HttpStatusCode.BadRequest,
                "otherwise a caller learns Secret by watching which entries come back");
        (await StatusAsync(type, $"q={Needle}&filter[Nope][eq]=1"))
            .Should().Be(HttpStatusCode.BadRequest, "an unknown field is refused, not ignored");
        (await StatusAsync(type, "q=a&filter[Secret][eq]=classified"))
            .Should().Be(HttpStatusCode.BadRequest, "and the answer does not depend on q being long enough to search");
    }

    [Fact]
    public async Task A_value_carrying_sql_is_compared_and_matches_nothing()
    {
        var type = await SeedAsync();

        var hostile = await SearchAsync(type, "limit=50&filter[Category][eq]=" + Uri.EscapeDataString("' or 1=1 --"));
        hostile.Slugs.Should().BeEmpty("the value is data, so it matches no category");

        var honest = await SearchAsync(type, "limit=50&filter[Category][eq]=sports");
        honest.Slugs.Should().HaveCount(3);
        honest.Slugs.Should().BeEquivalentTo(new[] { "sports-1", "sports-2", "sports-3" },
            "a real value still matches, so the empty result above means something");
    }

    [Fact]
    public async Task Sort_is_still_ignored_by_search()
    {
        var type = await SeedAsync();

        var results = await SearchAsync(type, "limit=50&sort=Nope");

        results.Slugs.Should().HaveCount(4, "an unknown sort is a 400 on the list and was never read here");
    }
}
