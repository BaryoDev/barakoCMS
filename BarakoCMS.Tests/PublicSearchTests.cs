using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Features.Content.Create;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class PublicSearchTests(IntegrationTestFixture factory)
{
    private const string Needle = "zephyrium";
    private readonly HttpClient _client = factory.CreateClient();

    private async Task StoreContentTypeAsync(string type)
    {
        using var scope = factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        s.Store(new ContentTypeDefinition
        {
            IsPubliclyDeliverable = true,
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = new()
            {
                new() { Name = "Title", DisplayName = "Title", Type = "string" },
                new() { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new() { Name = "Body", DisplayName = "Body", Type = "markdown" },
                new() { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive }
            }
        });

        await s.SaveChangesAsync();
    }

    private async Task SeedAsync(string type)
    {
        await StoreContentTypeAsync(type);

        using var scope = factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var publicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Title", "Slug", "Body" };

        void AddContent(Dictionary<string, object> data, ContentStatus status = ContentStatus.Published, SensitivityLevel sens = SensitivityLevel.Public) =>
            s.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = status,
                Sensitivity = sens,
                Data = data,
                SearchText = string.Join(' ', data.Where(kv => publicKeys.Contains(kv.Key) && kv.Value is string v && !string.IsNullOrWhiteSpace(v)).Select(kv => kv.Value))
            });

        AddContent(new() { ["Title"] = $"About {Needle}", ["Slug"] = "title-hit", ["Body"] = "plain" });
        AddContent(new() { ["Title"] = "Nothing special", ["Slug"] = "body-hit", ["Body"] = $"a paragraph mentioning {Needle} once" });
        AddContent(new() { ["Title"] = $"Draft {Needle}", ["Slug"] = "draft-hit", ["Body"] = Needle }, status: ContentStatus.Draft);
        AddContent(new() { ["Title"] = $"Sensitive {Needle}", ["Slug"] = "sens-hit", ["Body"] = Needle }, sens: SensitivityLevel.Sensitive);
        AddContent(new() { ["Title"] = "Clean title", ["Slug"] = "field-hit", ["Body"] = "clean body", ["Secret"] = Needle });

        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task Search_ReturnsPublishedPublicMatches_ExcludingDraftsSensitiveAndHiddenFields()
    {
        var type = $"searchpub_{Guid.NewGuid():N}";
        await StoreContentTypeAsync(type);

        var (adminToken, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var createRes = await adminClient.PostAsJsonAsync("/api/contents", new Request
        {
            ContentType = type,
            Status = ContentStatus.Published,
            Data = new() { ["Title"] = $"About {Needle}", ["Slug"] = "title-hit", ["Body"] = "plain" }
        });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await _client.GetAsync($"/api/public/{type}/search?q={Needle}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        (await res.Content.ReadAsStringAsync()).Should().Contain("title-hit");
    }
}
