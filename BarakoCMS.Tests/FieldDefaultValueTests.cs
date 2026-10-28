using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>A field's DefaultValue fills a key the caller left out when an entry is created.</summary>
[Collection("Sequential")]
public class FieldDefaultValueTests
{
    private readonly IntegrationTestFixture _factory;

    public FieldDefaultValueTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<string> SeedTypeAsync(string suffix)
    {
        var type = $"fielddefault_{suffix}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "text" },
                new FieldDefinition { Name = "State", DisplayName = "State", Type = "text", DefaultValue = "Active" },
            ],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    private static string? StateOf(Content c) => c.Data.TryGetValue("State", out var v) ? v?.ToString() : null;

    [Fact]
    public async Task A_create_without_the_key_stores_the_default()
    {
        var type = await SeedTypeAsync("create");
        var client = await AdminAsync();

        var resp = await client.PostAsJsonAsync("/api/contents",
            new { contentType = type, status = ContentStatus.Published, data = new Dictionary<string, object> { ["Name"] = "Ana" } }, Ct);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync(Ct));

        StateOf((await EntriesAsync(type)).Single()).Should().Be("Active");
    }

    [Theory]
    [InlineData("Lifetime")]
    [InlineData("")]
    public async Task An_explicit_value_is_kept(string sent)
    {
        var type = await SeedTypeAsync("keep" + sent.Length);
        var client = await AdminAsync();

        var resp = await client.PostAsJsonAsync("/api/contents",
            new { contentType = type, status = ContentStatus.Published, data = new Dictionary<string, object> { ["Name"] = "Ana", ["State"] = sent } }, Ct);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync(Ct));

        StateOf((await EntriesAsync(type)).Single()).Should().Be(sent);
    }

    [Fact]
    public async Task An_import_row_without_the_key_stores_the_default()
    {
        var type = await SeedTypeAsync("import");
        var client = await AdminAsync();

        var resp = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            records = new[]
            {
                new Dictionary<string, object> { ["Name"] = "Ana" },
                new Dictionary<string, object> { ["Name"] = "Ben", ["State"] = "Lifetime" },
            },
        }, Ct);
        var body = await resp.Content.ReadAsStringAsync(Ct);
        resp.IsSuccessStatusCode.Should().BeTrue(body);
        JsonDocument.Parse(body).RootElement.GetProperty("created").GetInt32().Should().Be(2);

        var entries = await EntriesAsync(type);
        StateOf(entries.Single(e => e.Data["Name"].ToString() == "Ana")).Should().Be("Active");
        StateOf(entries.Single(e => e.Data["Name"].ToString() == "Ben")).Should().Be("Lifetime");
    }
}
