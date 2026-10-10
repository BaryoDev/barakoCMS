using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// An int field takes any Int64 over HTTP, filters and sorts on it in delivery, and refuses a
/// fraction or a number past Int64 with a 400 naming the field (#706).
/// </summary>
/// <remarks>
/// Bodies are written as raw JSON so the numbers on the wire are exactly the ones under test: a C#
/// long cannot hold the value past Int64 that the refusal is about.
/// </remarks>
[Collection("Sequential")]
public class IntFieldRangeIntegrationTests
{
    private readonly IntegrationTestFixture _factory;

    public IntFieldRangeIntegrationTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<string> SeedTypeAsync()
    {
        var type = $"intrange{Guid.NewGuid():N}"[..20];

        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Views", DisplayName = "Views", Type = "int" },
            ],
        });
        await s.SaveChangesAsync();
        return type;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, string title, string views) =>
        client.PostAsync("/api/contents", new StringContent(
            $"{{\"contentType\":\"{type}\",\"status\":\"Published\",\"data\":{{\"Title\":\"{title}\",\"Views\":{views}}}}}",
            Encoding.UTF8,
            "application/json"));

    private async Task<List<string>> TitlesAsync(string url)
    {
        var res = await _factory.CreateClient().GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<Page>();
        return body!.Items.Select(i => i.Data["Title"].ToString()!).ToList();
    }

    private sealed record Item(Dictionary<string, object> Data);
    private sealed record Page(List<Item> Items);

    [Fact]
    public async Task A_value_past_Int32_is_stored_and_filters_and_sorts_as_a_number()
    {
        var type = await SeedTypeAsync();
        var admin = await AdminAsync();

        (await CreateAsync(admin, type, "small", "5")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(admin, type, "big", "3000000000")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(admin, type, "bigger", "9000000000000")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TitlesAsync($"/api/public/{type}?sort=Views")).Should().Equal("small", "big", "bigger");
        (await TitlesAsync($"/api/public/{type}?sort=-Views")).Should().Equal("bigger", "big", "small");
        (await TitlesAsync($"/api/public/{type}?filter[Views][gt]=2147483647&sort=Views")).Should().Equal("big", "bigger");
        (await TitlesAsync($"/api/public/{type}?filter[Views][eq]=3000000000")).Should().Equal("big");
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("1.5")]
    public async Task A_value_past_Int64_or_a_fraction_is_refused_naming_the_field(string views)
    {
        var type = await SeedTypeAsync();
        var admin = await AdminAsync();

        var res = await CreateAsync(admin, type, "refused", views);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("Views");
        (await TitlesAsync($"/api/public/{type}")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_stored_Int32_value_still_reads_and_sorts_beside_an_Int64_one()
    {
        var type = await SeedTypeAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = "stored", ["Views"] = 7 },
            });
            await s.SaveChangesAsync();
        }

        var admin = await AdminAsync();
        (await CreateAsync(admin, type, "big", "3000000000")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await TitlesAsync($"/api/public/{type}?sort=-Views")).Should().Equal("big", "stored");
        (await TitlesAsync($"/api/public/{type}?filter[Views][eq]=7")).Should().Equal("stored");
    }
}
