using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.AI;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>GET /api/public/{type}/semantic</c> takes the delivery list's filters, applies them before
/// the scan cap and the ranking, and refuses one on a field that is not Public.
/// </summary>
[Collection("Sequential")]
public class SemanticSearchFilterTests
{
    private const int ScanLimit = 2;
    private const string Words = "photovoltaic renewable sunlight energy generation";

    private readonly HttpClient _client;
    private readonly IServiceProvider _services;

    public SemanticSearchFilterTests(IntegrationTestFixture factory)
    {
        var derived = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.PostConfigure<AiOptions>(o => o.SemanticSearchScanLimit = ScanLimit)));
        _client = derived.CreateClient();
        _services = derived.Services;
    }

    // Every entry carries the same vector, so ranking cannot tell them apart and only the filter
    // decides which come back.
    private async Task<string> SeedAsync(int sports, int news)
    {
        var type = "semfilt-" + Guid.NewGuid().ToString("n")[..8];
        var vector = await new FakeEmbeddingClient().EmbedAsync(Words, TestContext.Current.CancellationToken);

        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new FieldDefinition { Name = "Category", Type = "string", Sensitivity = SensitivityLevel.Public },
                new FieldDefinition { Name = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
        });

        void Add(string category, int i)
        {
            var id = Guid.NewGuid();
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object>
                {
                    ["Title"] = $"Solar {category} {i}",
                    ["Category"] = category,
                    ["Secret"] = "classified",
                },
            });
            session.Store(new ContentEmbedding
            {
                Id = id,
                ContentType = type,
                Slug = $"{category}-{i}",
                Title = $"Solar {category} {i}",
                Vector = vector!,
            });
        }

        for (var i = 0; i < sports; i++) Add("sports", i);
        for (var i = 0; i < news; i++) Add("news", i);

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private Task<HttpResponseMessage> GetAsync(string type, string query) =>
        _client.GetAsync(
            $"/api/public/{type}/semantic?q={Uri.EscapeDataString(Words)}&{query}",
            TestContext.Current.CancellationToken);

    private async Task<(List<string> Slugs, bool Truncated)> SearchAsync(string type, string query)
    {
        var response = await GetAsync(type, query);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return (
            body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("slug").GetString()!).ToList(),
            body.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_filter_is_applied_before_the_scan_cap_and_the_ranking()
    {
        // Five embeddings against a scan cap of two, with the one news entry among them.
        var type = await SeedAsync(sports: 4, news: 1);

        var unfiltered = await SearchAsync(type, "limit=10");
        unfiltered.Slugs.Should().HaveCount(ScanLimit, "the unfiltered scan stops at the cap");
        unfiltered.Truncated.Should().BeTrue();

        var news = await SearchAsync(type, "limit=10&filter[Category][eq]=news");
        news.Slugs.Should().HaveCount(1);
        news.Slugs.Should().Equal("news-0");
        news.Truncated.Should().BeFalse("one entry matches, which is under the cap, so nothing was cut");
    }

    [Fact]
    public async Task A_filter_matching_more_entries_than_the_scan_cap_says_it_was_truncated()
    {
        var type = await SeedAsync(sports: 4, news: 1);

        var sports = await SearchAsync(type, "limit=10&filter[Category][eq]=sports");

        sports.Slugs.Should().HaveCount(ScanLimit);
        sports.Slugs.Should().OnlyContain(slug => slug.StartsWith("sports-"));
        sports.Truncated.Should().BeTrue("four entries match and two were ranked");
    }

    [Fact]
    public async Task A_filter_on_a_field_that_is_not_public_is_refused()
    {
        var type = await SeedAsync(sports: 1, news: 1);

        (await GetAsync(type, "filter[Secret][eq]=classified")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest,
                "otherwise a caller learns Secret by watching which entries come back");
        (await GetAsync(type, "filter[Nope][eq]=1")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "an unknown field is refused, not ignored");
    }

    [Fact]
    public async Task A_value_carrying_sql_is_compared_and_matches_nothing()
    {
        var type = await SeedAsync(sports: 1, news: 1);

        var hostile = await SearchAsync(type, "limit=10&filter[Category][eq]=" + Uri.EscapeDataString("' or 1=1 --"));
        hostile.Slugs.Should().BeEmpty("the value is data, so it matches no category");

        var honest = await SearchAsync(type, "limit=10&filter[Category][eq]=sports");
        honest.Slugs.Should().HaveCount(1);
        honest.Slugs.Should().Equal("sports-0");
    }
}
