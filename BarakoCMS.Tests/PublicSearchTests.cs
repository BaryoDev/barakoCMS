using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class PublicSearchTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public PublicSearchTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // anonymous
    }

    private const string Needle = "zephyrium";

    private async Task SeedAsync(string type)
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
                new() { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive }
            }
        });

        var publicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Title", "Slug", "Body"
        };

        void AddContent(
            Dictionary<string, object> data,
            ContentStatus status = ContentStatus.Published,
            SensitivityLevel sens = SensitivityLevel.Public) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                Sensitivity = sens,
                Data = data,
                SearchText = string.Join(
                    ' ',
                    data.Where(kv =>
                            publicKeys.Contains(kv.Key) &&
                            kv.Value is string v &&
                            !string.IsNullOrWhiteSpace(v))
                        .Select(kv => kv.Value))
            });

        AddContent(new()
        {
            ["Title"] = $"About {Needle}",
            ["Slug"] = "title-hit",
            ["Body"] = "plain"
        });

        AddContent(new()
        {
            ["Title"] = "Nothing special",
            ["Slug"] = "body-hit",
            ["Body"] = $"a paragraph mentioning {Needle} once"
        });

        AddContent(new()
        {
            ["Title"] = $"Draft {Needle}",
            ["Slug"] = "draft-hit",
            ["Body"] = Needle
        }, status: ContentStatus.Draft);

        AddContent(new()
        {
            ["Title"] = $"Sensitive {Needle}",
            ["Slug"] = "sens-hit",
            ["Body"] = Needle
        }, sens: SensitivityLevel.Sensitive);

        AddContent(new()
        {
            ["Title"] = "Clean title",
            ["Slug"] = "field-hit",
            ["Body"] = "clean body",
            ["Secret"] = Needle
        });

        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_ReturnsPublishedPublicMatches_ExcludingDraftsSensitiveAndHiddenFields()
    {
        var type = $"searchpub_{Guid.NewGuid():N}";

        // Set up the content type only.
        using (var scope = _factory.Services.CreateScope())
        {
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
                    }
                }
            });

            await s.SaveChangesAsync();
        }

        // Create the searchable content through the real production endpoint.
        var (adminToken, _) = await CreateAdminUserAsync();

        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);

        var createRes = await adminClient.PostAsJsonAsync("/api/contents",
            new barakoCMS.Features.Content.Create.Request
            {
                ContentType = type,
                Status = ContentStatus.Published,
                Data = new Dictionary<string, object>
                {
                    ["Title"] = $"About {Needle}",
                    ["Slug"] = "title-hit",
                    ["Body"] = "plain"
                }
            });

        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // Anonymous search.
        var res = await _client.GetAsync($"/api/public/{type}/search?q={Needle}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("title-hit",
            "content created through the real endpoint must be searchable");
    }
    private async Task<(string token, Guid userId)> CreateAdminUserAsync()
    {
        return await TestHelpers.CreateAdminUserAsync(_factory);
    }

}
