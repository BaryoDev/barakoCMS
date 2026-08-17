using Xunit;
using FluentAssertions;
using System.Net;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

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

    public PublicSearchTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient(); // anonymous
    }

    private const string Needle = "zephyrium"; // unlikely token so matches are unambiguous

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
                new FieldDefinition { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            },
        });
        // title match, body match, a draft, a Sensitive doc, and a Sensitive-field-only match.
        s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = $"About {Needle}", ["Slug"] = "title-hit", ["Body"] = "plain" } });
        s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = "Nothing special", ["Slug"] = "body-hit", ["Body"] = $"a paragraph mentioning {Needle} once" } });
        s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Draft, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = $"Draft {Needle}", ["Slug"] = "draft-hit", ["Body"] = Needle } });
        s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Sensitive,
            Data = new() { ["Title"] = $"Sensitive {Needle}", ["Slug"] = "sens-hit", ["Body"] = Needle } });
        s.Store(new Content { Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published, Sensitivity = SensitivityLevel.Public,
            Data = new() { ["Title"] = "Clean title", ["Slug"] = "field-hit", ["Body"] = "clean body", ["Secret"] = Needle } });
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
}
