using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// An <c>inlineimage</c> field over HTTP: written through <c>POST /api/contents</c>, delivered
/// anonymously as it was sent, refused with a 400 that does not repeat the value, and left out of the
/// entry's search text.
/// </summary>
/// <remarks>
/// Every type and slug is unique to its test, and an entry is found by its own slug.
/// </remarks>
[Collection("Sequential")]
public class InlineImageDeliveryTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _admin;
    private readonly HttpClient _anonymous;

    public InlineImageDeliveryTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _admin = fixture.CreateClient();
        _anonymous = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _admin.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Png => InlineImageFieldTests.DataUri("image/png", InlineImageFieldTests.Png(32, 32));

    private async Task<string> StoreTypeAsync()
    {
        var name = "inlineimg-" + Guid.NewGuid().ToString("n")[..10];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
                new FieldDefinition { Name = "Logo", DisplayName = "Logo", Type = "inlineimage" },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private Task<HttpResponseMessage> CreateAsync(string type, string slug, object logo) =>
        _admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status = "Published",
            data = new Dictionary<string, object> { ["Title"] = "Acme", ["Slug"] = slug, ["Logo"] = logo },
        }, Ct);

    private async Task<JsonElement> DeliveredDataAsync(string type, string slug)
    {
        var response = await _anonymous.GetAsync($"/api/public/{type}/{slug}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return doc.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task An_inline_image_written_through_the_api_is_delivered_as_it_was_sent()
    {
        var type = await StoreTypeAsync();

        var created = await CreateAsync(type, "acme", new { url = Png, alt = "Acme logo" });
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(Ct));

        var data = await DeliveredDataAsync(type, "acme");

        var logo = data.GetProperty("Logo");
        logo.GetProperty("url").GetString().Should().Be(Png);
        logo.GetProperty("alt").GetString().Should().Be("Acme logo");
    }

    [Fact]
    public async Task An_svg_is_refused_with_a_400_that_names_the_field_and_does_not_repeat_the_value()
    {
        var type = await StoreTypeAsync();
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"));

        var response = await CreateAsync(type, "svg", new { url = $"data:image/svg+xml;base64,{payload}" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Logo").And.Contain("SVG is refused").And.Contain("64 KB");
        body.Should().NotContain(payload[..16], "the start of the payload, which has no character JSON escapes")
            .And.NotContain("script");
    }

    [Fact]
    public async Task A_stored_value_that_is_not_an_allowed_image_is_left_out_of_delivery_and_the_entry_still_reads()
    {
        var type = await StoreTypeAsync();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object>
                {
                    ["Title"] = "Written directly",
                    ["Slug"] = "direct",
                    ["Logo"] = new Dictionary<string, object> { ["url"] = "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=" },
                },
            });
            await session.SaveChangesAsync(Ct);
        }

        var data = await DeliveredDataAsync(type, "direct");
        data.GetProperty("Title").GetString().Should().Be("Written directly");
        data.TryGetProperty("Logo", out _).Should().BeFalse("only a data URI of an allowed image type is delivered");

        var list = await _anonymous.GetAsync($"/api/public/{type}", Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await list.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Written directly").And.NotContain("svg+xml");
    }

    [Fact]
    public async Task The_search_text_of_a_new_entry_leaves_the_image_out()
    {
        var type = await StoreTypeAsync();
        var logo = JsonDocument.Parse(JsonSerializer.Serialize(new { url = Png, alt = "Acme logo" })).RootElement.Clone();

        using var scope = _fixture.Services.CreateScope();
        var creator = scope.ServiceProvider.GetRequiredService<IContentCreator>();

        var created = await creator.StageAsync(
            new ContentCreateRequest
            {
                ContentType = type,
                Data = new Dictionary<string, object> { ["Title"] = "Searchable title", ["Slug"] = "search", ["Logo"] = logo },
            },
            Guid.NewGuid(), batch: null, Ct);

        created.SearchText.Should().Contain("Searchable title", "the other Public fields are still searchable");
        created.SearchText.Should().NotContain("base64").And.NotContain("Acme logo");
    }
}
