using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// SearchText has to carry the words of a list or object value, not the CLR type name or raw JSON.
/// </summary>
[Collection("Sequential")]
public class SearchTextFlatteningTests
{
    private readonly IntegrationTestFixture _factory;

    public SearchTextFlatteningTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> NewTypeAsync(HttpClient client)
    {
        var name = "st" + Guid.NewGuid().ToString("N")[..10];
        var response = await client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Search text probe",
            fields = new object[]
            {
                new { name = "Title", type = "string", sensitivity = "Public" },
                new { name = "Places", type = "array", sensitivity = "Public" },
                new { name = "Address", type = "json", sensitivity = "Public" },
                new { name = "Count", type = "number", sensitivity = "Public" },
            },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return name;
    }

    private static Dictionary<string, object> FullData() => new()
        {
            ["Title"] = "Dr Reyes",
            ["Places"] = new[] { "Northside Clinic", "Harbor Health" },
            ["Address"] = new Dictionary<string, object>
            {
                ["street"] = "12 Mango Road",
                ["geo"] = new Dictionary<string, object> { ["city"] = "Koronadal", ["floor"] = 3 },
            },
            ["Count"] = 7,
        };

    private static object Payload(string type) => new { contentType = type, status = "Published", data = FullData() };

    private async Task<string> SearchTextOfAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await session.LoadAsync<Content>(id);
        content.Should().NotBeNull();
        return content!.SearchText ?? "";
    }

    private static void AssertFlattened(string text)
    {
        text.Should().Contain("Dr Reyes");
        text.Should().Contain("Northside Clinic").And.Contain("Harbor Health");
        text.Should().Contain("12 Mango Road").And.Contain("Koronadal");
        text.Should().Contain("3").And.Contain("7");
        text.Should().NotContain("System.");
        text.Should().NotContain("`1");
        text.Should().NotContain("{").And.NotContain("[").And.NotContain("\"");
    }

    [Fact]
    public async Task Create_puts_list_and_object_words_in_search_text()
    {
        var client = await ClientAsync();
        var type = await NewTypeAsync(client);

        var created = await client.PostAsJsonAsync("/api/contents", Payload(type));
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        AssertFlattened(await SearchTextOfAsync(body.RootElement.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Update_puts_list_and_object_words_in_search_text()
    {
        var client = await ClientAsync();
        var type = await NewTypeAsync(client);

        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status = "Published",
            data = new Dictionary<string, object> { ["Title"] = "Dr Reyes" },
        });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/contents/{id}", new { data = FullData() });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        AssertFlattened(await SearchTextOfAsync(id));
    }
}
