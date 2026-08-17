using Xunit;
using FluentAssertions;
using System.Net;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Navigation menus are now a "menu" content type served through public delivery, not a bespoke CRUD
/// surface. These tests pin that a menu with a nested <c>json</c> items field round-trips through the
/// anonymous public endpoint, and that draft/Sensitive/missing menus are not exposed (same rules as any
/// other content type).
/// </summary>
[Collection("Sequential")]
public class MenuTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client; // anonymous — public delivery needs no auth

    public MenuTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /* A "menu" content type with a Name string and an Items json field holding a nested nav tree. */
    private async Task SeedMenuAsync(string slug, ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        if (!await s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == "menu"))
        {
            s.Store(new ContentTypeDefinition
            {
            IsPubliclyDeliverable = true,
                Id = Guid.NewGuid(),
                Name = "menu",
                DisplayName = "Menu",
                Fields = new()
                {
                    new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                    new FieldDefinition { Name = "Items", DisplayName = "Items", Type = "json" },
                },
            });
        }

        s.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = "menu", Status = status, Sensitivity = sensitivity,
            Data = new()
            {
                ["Name"] = "Main",
                ["Slug"] = slug,
                ["Items"] = new object[]
                {
                    new Dictionary<string, object> { ["Label"] = "Blog", ["Url"] = "/blog", ["OpenInNewTab"] = false },
                    new Dictionary<string, object>
                    {
                        ["Label"] = "Docs", ["Url"] = "/docs", ["OpenInNewTab"] = false,
                        ["Children"] = new object[]
                        {
                            new Dictionary<string, object> { ["Label"] = "Guide", ["Url"] = "/docs/guide" },
                        },
                    },
                },
            },
        });
        await s.SaveChangesAsync();
    }

    [Fact]
    public async Task PublishedMenu_IsDelivered_WithNestedItems_Anonymously()
    {
        await SeedMenuAsync("main");

        var res = await _client.GetAsync("/api/public/menu/main");
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: "a menu is a content type served publicly");
        var body = await res.Content.ReadAsStringAsync();

        body.Should().Contain("Blog").And.Contain("/blog");
        body.Should().Contain("Docs");
        body.Should().Contain("Guide", "a nested json items array round-trips through public delivery");
        res.Headers.CacheControl?.Public.Should().BeTrue("public reads are CDN-cacheable");
    }

    [Fact]
    public async Task DraftMenu_IsNotExposed()
    {
        await SeedMenuAsync("draft-menu", status: ContentStatus.Draft);
        var res = await _client.GetAsync("/api/public/menu/draft-menu");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a draft menu is not addressable publicly");
    }

    [Fact]
    public async Task SensitiveMenu_IsNotExposed()
    {
        await SeedMenuAsync("secret-menu", sensitivity: SensitivityLevel.Sensitive);
        var res = await _client.GetAsync("/api/public/menu/secret-menu");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "a Sensitive menu is not public");
    }

    [Fact]
    public async Task MissingMenu_Is404()
    {
        var res = await _client.GetAsync("/api/public/menu/does-not-exist");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
