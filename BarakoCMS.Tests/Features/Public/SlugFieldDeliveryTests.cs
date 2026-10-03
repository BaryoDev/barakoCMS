using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Delivery takes an entry's slug from a field of type slug, or from a Public text field named
/// Slug. A token or a Hidden field that happens to be named Slug is not a slug, so its value is not
/// served as one, and the type has no slug route.
/// </summary>
/// <remarks>
/// The type and the entry are stored directly, so the stored value is one the test knows and the
/// definition is one no endpoint would accept for a token. Each test checks the entry's Public
/// field is delivered, so a missing value means the slug rule and not a missing entry.
/// </remarks>
[Collection("Sequential")]
public class SlugFieldDeliveryTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public SlugFieldDeliveryTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Type, Guid Id, string Marker)> StoreAsync(FieldDefinition slug)
    {
        var type = "slugrule" + Guid.NewGuid().ToString("n")[..10];
        var marker = "keepout" + Guid.NewGuid().ToString("n");
        var id = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                slug,
            ],
        });
        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "Open day", ["Slug"] = marker },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync(Ct);
        return (type, id, marker);
    }

    private async Task<string> OkBodyAsync(string path)
    {
        var response = await _client.GetAsync(path, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private async Task AssertNotServedAsync(FieldDefinition slug)
    {
        var (type, id, marker) = await StoreAsync(slug);

        var list = await OkBodyAsync($"/api/public/{type}?pageSize=100");
        var items = JsonDocument.Parse(list).RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetGuid() == id)
            .ToList();
        items.Should().HaveCount(1, "the entry is delivered, so what follows is about its slug");
        items[0].GetProperty("data").GetProperty("Title").GetString().Should().Be("Open day");
        list.Should().NotContain(marker);

        (await _client.GetAsync($"/api/public/{type}/{marker}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the type has no slug, so the value finds nothing");

        var feed = await OkBodyAsync($"/api/public/{type}/feed.xml");
        feed.Should().Contain(id.ToString(), "the entry is in the feed");
        feed.Should().NotContain(marker);

        (await OkBodyAsync("/api/public/sitemap.xml")).Should().NotContain(marker);
    }

    [Fact]
    public async Task A_token_named_slug_is_not_served_as_the_slug()
    {
        await AssertNotServedAsync(new FieldDefinition
        {
            Name = "Slug", DisplayName = "Slug", Type = "token", Sensitivity = SensitivityLevel.Hidden,
        });
    }

    [Fact]
    public async Task A_hidden_text_field_named_slug_is_not_served_as_the_slug()
    {
        await AssertNotServedAsync(new FieldDefinition
        {
            Name = "Slug", DisplayName = "Slug", Type = "string", Sensitivity = SensitivityLevel.Hidden,
        });
    }

    [Fact]
    public async Task A_public_text_field_named_slug_is_still_the_slug()
    {
        var (type, id, marker) = await StoreAsync(new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "string" });

        var entry = JsonDocument.Parse(await OkBodyAsync($"/api/public/{type}/{marker}")).RootElement;

        entry.GetProperty("id").GetGuid().Should().Be(id);
        entry.GetProperty("slug").GetString().Should().Be(marker);
    }
}
