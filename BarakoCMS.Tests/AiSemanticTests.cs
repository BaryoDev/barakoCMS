using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// AI semantic search (POST /api/ai/index/{type} + GET /api/public/{type}/semantic) with a deterministic
/// fake embedder. Adversarial: search returns the semantically-nearest Published, document-Public entry
/// and never a draft, a Sensitive document, or an entry unpublished after indexing.
/// </summary>
[Collection("Sequential")]
public class AiSemanticTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public AiSemanticTests(IntegrationTestFixture factory)
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
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Body", DisplayName = "Body", Type = "markdown" },
            },
        });
        void Doc(string slug, string title, string body, ContentStatus st, SensitivityLevel sev) =>
            s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = st, Sensitivity = sev,
                Data = new() { ["Title"] = title, ["Slug"] = slug, ["Body"] = body } });

        Doc("solar", "Solar panels", "photovoltaic renewable sunlight energy generation", ContentStatus.Published, SensitivityLevel.Public);
        Doc("coffee", "Coffee brewing", "espresso beans roast grinder barista", ContentStatus.Published, SensitivityLevel.Public);
        Doc("solar-draft", "Solar draft", "photovoltaic renewable sunlight energy generation", ContentStatus.Draft, SensitivityLevel.Public);
        Doc("solar-secret", "Solar secret", "photovoltaic renewable sunlight energy generation", ContentStatus.Published, SensitivityLevel.Sensitive);
        await s.SaveChangesAsync();
    }

    private string AdminToken() => _factory.CreateToken(new[] { "SuperAdmin" }, Guid.NewGuid().ToString());

    [Fact]
    public async Task Index_ThenSemanticSearch_ReturnsNearestPublicMatch()
    {
        var type = "ai_solar";
        await SeedAsync(type);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken());
        var index = await _client.PostAsync($"/api/ai/index/{type}", null);
        index.StatusCode.Should().Be(HttpStatusCode.OK, because: await index.Content.ReadAsStringAsync());
        (await index.Content.ReadAsStringAsync()).Should().Contain("\"indexed\":2", "only the 2 published+public entries are indexed");

        _client.DefaultRequestHeaders.Authorization = null; // search is anonymous
        var res = await _client.GetAsync($"/api/public/{type}/semantic?q=photovoltaic%20renewable%20sunlight");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("\"solar\"", "the solar entry is the nearest match");
        body.Should().NotContain("\"coffee\"", "the unrelated entry is below the similarity floor");
        body.Should().NotContain("solar-draft", "a draft is never indexed or returned");
        body.Should().NotContain("solar-secret", "a Sensitive entry is never indexed or returned");
    }

    [Fact]
    public async Task SemanticSearch_ExcludesEntryUnpublishedAfterIndexing()
    {
        var type = "ai_fresh";
        await SeedAsync(type);

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken());
        await _client.PostAsync($"/api/ai/index/{type}", null);

        // Unpublish the solar entry AFTER indexing; its vector still exists but it must not surface.
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var all = await s.Query<Content>().Where(c => c.ContentType == type).ToListAsync();
            var solar = all.First(c => c.Data.TryGetValue("Slug", out var v) && v?.ToString() == "solar");
            solar.Status = ContentStatus.Draft;
            s.Store(solar);
            await s.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.GetAsync($"/api/public/{type}/semantic?q=photovoltaic%20renewable%20sunlight");
        (await res.Content.ReadAsStringAsync()).Should().NotContain("\"solar\"", "a since-unpublished entry is filtered at query time");
    }
}
