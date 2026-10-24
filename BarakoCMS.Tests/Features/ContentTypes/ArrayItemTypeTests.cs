using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// An <c>array</c> field may declare an <c>itemType</c>, and a write then refuses a list holding an
/// item of another type. An array that declares none accepts any list, as before.
/// </summary>
[Collection("Integration1")]
public class ArrayItemTypeTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ArrayItemTypeTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NewName() => "arritems" + Guid.NewGuid().ToString("n")[..10];

    private async Task<string> CreateTypeAsync(params object[] fields)
    {
        var name = NewName();
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, isPubliclyDeliverable = true, fields,
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return name;
    }

    private Task<HttpResponseMessage> WriteAsync(string type, object links) =>
        _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "t", ["Links"] = links },
        });

    [Fact]
    public async Task An_array_of_urls_refuses_an_item_that_is_not_a_url_and_names_its_index()
    {
        var type = await CreateTypeAsync(
            new { name = "Title", type = "string" },
            new { name = "Links", type = "array", itemType = "url" });

        var res = await WriteAsync(type, new object[] { "https://a.example", "not a url" });

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("index 1").And.Contain("'url'");
    }

    [Fact]
    public async Task An_array_of_ints_refuses_text_and_null_items()
    {
        var type = await CreateTypeAsync(
            new { name = "Title", type = "string" },
            new { name = "Links", type = "array", itemType = "int" });

        (await WriteAsync(type, new object[] { 1, "two" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WriteAsync(type, new object?[] { 1, null })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WriteAsync(type, new object[] { 1, 2, 3 })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_list_of_valid_items_is_stored()
    {
        var type = await CreateTypeAsync(
            new { name = "Title", type = "string" },
            new { name = "Links", type = "array", itemType = "url" });

        var res = await WriteAsync(type, new[] { "https://a.example", "https://b.example" });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_array_with_no_item_type_still_accepts_any_list()
    {
        var type = await CreateTypeAsync(
            new { name = "Title", type = "string" },
            new { name = "Links", type = "array" });

        var res = await WriteAsync(type, new object[] { "not a url", 4, true });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("string", "url")]
    [InlineData("array", "json")]
    [InlineData("array", "reference")]
    [InlineData("array", "Url")]
    public async Task An_item_type_is_refused_off_an_array_or_outside_the_scalar_types(string type, string itemType)
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "x",
            fields = new object[] { new { name = "Links", type, itemType } },
        });

        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("itemType");
    }

    [Fact]
    public async Task Adding_a_field_takes_an_item_type()
    {
        var type = await CreateTypeAsync(new { name = "Title", type = "string" });

        var add = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Links", type = "array", itemType = "email",
        });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());

        (await WriteAsync(type, new[] { "nobody" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await WriteAsync(type, new[] { "a@b.example" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delivery_openapi_describes_the_items_of_a_typed_array()
    {
        var type = await CreateTypeAsync(
            new { name = "Title", type = "string" },
            new { name = "Links", type = "array", itemType = "url" });

        using var doc = await OpenApiTagTests.FetchDocumentAsync(_fixture);

        var items = doc.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty($"Public{char.ToUpperInvariant(type[0])}{type[1..]}Fields")
            .GetProperty("properties").GetProperty("Links").GetProperty("items");
        items.GetProperty("format").GetString().Should().Be("uri");
    }
}
