using FluentAssertions;
using System.Net;

using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Tests;

/// <summary>
/// Public search (GET /api/public/{type}/search) over an ANONYMOUS client. Adversarial: it must match
/// only Published, document-Public entries, only over Public fields — a draft, a Sensitive document, and
/// a value stored in a Sensitive field must never surface. Title hits outrank body hits.
/// </summary>
[Collection("Sequential")]
public class PublicSearchTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;
    private async Task StoreContentTypeAsync(string type)
    {
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
                new()
                {
                    Name = "Secret",
                    DisplayName = "Secret",
                    Type = "string",
                    Sensitivity = SensitivityLevel.Sensitive
                },
                new() { Name = "Views", DisplayName = "Views", Type = "number" },

            }
        });

        await s.SaveChangesAsync();
    }
    public PublicSearchTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // anonymous
    }

    private const string Needle = "zephyrium"; // unlikely token so matches are unambiguous
    private async Task SeedAsync(string type)
    {
        await StoreContentTypeAsync(type);
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var publicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title", "Slug", "Body", "Views" };

        void AddContent(Dictionary<string, object> data, ContentStatus status = ContentStatus.Published, SensitivityLevel sens = SensitivityLevel.Public) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                Sensitivity = sens,
                Data = data,
                SearchText = string.Join(' ', data.Where(kv => publicKeys.Contains(kv.Key))
                    .Select(kv => kv.Value?.ToString())
                    .Where(v => !string.IsNullOrWhiteSpace(v)))
            });

        AddContent(new() { ["Title"] = $"About {Needle}", ["Slug"] = "title-hit", ["Body"] = "plain" });
        AddContent(new() { ["Title"] = "Nothing special", ["Slug"] = "body-hit", ["Body"] = $"a paragraph mentioning {Needle} once" });
        AddContent(new() { ["Title"] = $"Draft {Needle}", ["Slug"] = "draft-hit", ["Body"] = Needle }, status: ContentStatus.Draft);
        AddContent(new() { ["Title"] = $"Sensitive {Needle}", ["Slug"] = "sens-hit", ["Body"] = Needle }, sens: SensitivityLevel.Sensitive);
        AddContent(new() { ["Title"] = "Clean title", ["Slug"] = "field-hit", ["Body"] = "clean body", ["Secret"] = Needle });
        AddContent(new() { ["Title"] = "Numeric field", ["Slug"] = "numeric-hit", ["Body"] = "plain", ["Views"] = 12345 });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_ReturnsPublishedPublicMatches_ExcludingDraftsSensitiveAndHiddenFields()
    {
        var type = "searchpub_a";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/search?q={Needle}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("title-hit").And.Contain("body-hit", "published public matches are returned");
        body.Should().NotContain("draft-hit", "drafts are never searchable");
        body.Should().NotContain("sens-hit", "a document-Sensitive entry never surfaces");
        body.Should().NotContain("field-hit", "a match only in a Sensitive field must not surface");
        body.Should().NotContain(Needle + "\",\"Secret", "the Sensitive field value is never emitted");
    }

    [Fact]
    public async Task Search_RanksTitleAboveBody()
    {
        var type = "searchpub_rank";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/search?q={Needle}");
        var body = await res.Content.ReadAsStringAsync();
        body.IndexOf("title-hit", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("body-hit", StringComparison.Ordinal), "a title hit ranks first");
    }

    [Fact]
    public async Task Search_ShortQuery_ReturnsEmpty()
    {
        var type = "searchpub_short";
        await SeedAsync(type);

        var res = await _client.GetAsync($"/api/public/{type}/search?q=z");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"count\":0");
    }
    [Fact]
    public async Task Search_ContentCreatedThroughEndpoint_IsSearchable()
    {
        var type = $"searchpub_endpoint_{Guid.NewGuid():N}";
        await StoreContentTypeAsync(type);

        var (adminToken, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createRes = await adminClient.PostAsJsonAsync("/api/contents",
            new barakoCMS.Features.Content.Create.Request
            {
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new()
                {
                    ["Title"] = $"About {Needle}",
                    ["Slug"] = "endpoint-hit",
                    ["Body"] = "plain"
                }
            });

        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var searchRes = await _client.GetAsync(
            $"/api/public/{type}/search?q={Needle}");

        searchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        (await searchRes.Content.ReadAsStringAsync())
            .Should().Contain("endpoint-hit");
    }

    [Fact]
    public async Task Search_PublicNumericField_IsSearchable()
    {
        var type = $"searchpub_numeric_{Guid.NewGuid():N}";
        await SeedAsync(type);

        var res = await _client.GetAsync(
            $"/api/public/{type}/search?q=12345");

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("numeric-hit");
    }


}
