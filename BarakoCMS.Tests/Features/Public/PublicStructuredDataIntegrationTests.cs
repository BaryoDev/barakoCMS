using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// A read by slug carries schema.org JSON-LD for a type that declares a structured data type, and
/// the type is set through its own route (#567).
/// </summary>
[Collection("Sequential")]
public class PublicStructuredDataIntegrationTests
{
    private const string SecretAuthor = "Ana Secret-Reyes";

    private readonly IntegrationTestFixture _factory;

    public PublicStructuredDataIntegrationTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Field(
        string name, string type = "string", string? role = null,
        SensitivityLevel sensitivity = SensitivityLevel.Public) => new()
    {
        Name = name, DisplayName = name, Type = type, Role = role, Sensitivity = sensitivity,
    };

    private async Task<string> SeedAsync(string? structured)
    {
        var type = $"ld{Guid.NewGuid():n}"[..16];

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            StructuredDataType = structured,
            Fields =
            [
                Field("Slug", "slug"),
                Field("Headline", role: "title"),
                Field("Teaser", "text", role: "summary"),
                Field("Byline", role: "author", sensitivity: SensitivityLevel.Sensitive),
            ],
        });
        session.Store(new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new()
            {
                ["Slug"] = "flood",
                ["Headline"] = "Flood waters recede",
                ["Teaser"] = "The river is back inside its banks.",
                ["Byline"] = SecretAuthor,
            },
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<(JsonElement Root, string Raw)> ReadAsync(string type)
    {
        var res = await _factory.CreateClient().GetAsync($"/api/public/{type}/flood", Ct);
        var raw = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, raw);
        using var parsed = JsonDocument.Parse(raw);
        return (parsed.RootElement.Clone(), raw);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    [Fact]
    public async Task A_read_by_slug_carries_the_block_and_a_masked_field_is_not_in_it()
    {
        var type = await SeedAsync("NewsArticle");

        var (root, raw) = await ReadAsync(type);

        var block = root.GetProperty("structuredData");
        block.GetProperty("@context").GetString().Should().Be("https://schema.org");
        block.GetProperty("@type").GetString().Should().Be("NewsArticle");
        block.GetProperty("headline").GetString().Should().Be("Flood waters recede");
        block.GetProperty("description").GetString().Should().Be("The river is back inside its banks.");
        block.TryGetProperty("author", out _).Should().BeFalse("the author field is Sensitive");
        raw.Should().NotContain(SecretAuthor);
    }

    [Fact]
    public async Task A_type_that_declares_none_has_no_block_and_the_list_route_never_has_one()
    {
        var plain = await SeedAsync(null);
        (await ReadAsync(plain)).Raw.Should().NotContain("structuredData");

        var declared = await SeedAsync("Article");
        var list = await _factory.CreateClient().GetStringAsync($"/api/public/{declared}", Ct);
        list.Should().Contain("Flood waters recede").And.NotContain("structuredData");
    }

    [Fact]
    public async Task The_route_sets_and_clears_the_type_and_the_slug_read_and_description_follow()
    {
        var type = await SeedAsync(null);
        var admin = await AdminAsync();

        var set = await admin.PutAsJsonAsync($"/api/content-types/{type}/structured-data",
            new { structuredDataType = "Article" }, Ct);
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));

        (await ReadAsync(type)).Root.GetProperty("structuredData").GetProperty("@type").GetString().Should().Be("Article");

        var described = await _factory.CreateClient().GetStringAsync($"/api/public/types/{type}/description", Ct);
        using (var doc = JsonDocument.Parse(described))
        {
            doc.RootElement.GetProperty("structuredDataType").GetString().Should().Be("Article");
        }

        var cleared = await admin.PutAsJsonAsync($"/api/content-types/{type}/structured-data",
            new { structuredDataType = (string?)null }, Ct);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(type)).Raw.Should().NotContain("structuredData");
    }

    [Fact]
    public async Task An_unknown_type_is_refused_on_the_route_and_on_create_naming_the_accepted_ones()
    {
        var type = await SeedAsync(null);
        var admin = await AdminAsync();

        var refused = await admin.PutAsJsonAsync($"/api/content-types/{type}/structured-data",
            new { structuredDataType = "Recipe" }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("structuredDataType").And.Contain("Article");

        var create = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name = $"ldc{Guid.NewGuid():n}"[..16],
            displayName = "Story",
            structuredDataType = "article",
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);
        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await create.Content.ReadAsStringAsync(Ct)).Should().Contain("structuredDataType");
    }

    [Fact]
    public async Task Setting_the_type_needs_a_signed_in_caller_who_manages_content_types()
    {
        var type = await SeedAsync(null);

        var anonymous = await _factory.CreateClient().PutAsJsonAsync($"/api/content-types/{type}/structured-data",
            new { structuredDataType = "Article" }, Ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var editor = _factory.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Editor"));
        var forbidden = await editor.PutAsJsonAsync($"/api/content-types/{type}/structured-data",
            new { structuredDataType = "Article" }, Ct);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await ReadAsync(type)).Raw.Should().NotContain("structuredData");
    }
}
