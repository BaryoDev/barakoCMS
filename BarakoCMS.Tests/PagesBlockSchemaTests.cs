using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <c>GET /api/pages/block-schema</c> (#1257): the tenant's stored block schema in the shape
/// barakoPress publishes, with the <c>x-</c> hints a renderer may add, and a block's reserved
/// <c>dataSource</c> key.
/// </summary>
[Collection("Integration1")]
public class PagesBlockSchemaTests
{
    private const string Route = "/api/pages/block-schema";

    private readonly IntegrationTestFixture _fixture;

    public PagesBlockSchemaTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private async Task<(string Slug, HttpClient Admin)> TenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct));
    }

    private static async Task<JsonElement> ServedAsync(HttpClient client)
    {
        var res = await client.GetAsync(Route, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static async Task RegisterAsync(HttpClient admin, string blocksJson)
    {
        var res = await admin.PutAsync("/api/blocks/schema",
            new StringContent($$"""{ "blocks": {{blocksJson}} }""", System.Text.Encoding.UTF8, "application/json"), Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    private static JsonElement Block(JsonElement body, string type) =>
        body.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("type").GetString() == type);

    // The route

    [Fact]
    public async Task The_route_requires_a_signed_in_caller()
    {
        var (slug, _) = await TenantAsync();

        var res = await TenantProfileSeed.Anonymous(_fixture, slug).GetAsync(Route, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_tenant_with_no_schema_gets_an_empty_blocks_list()
    {
        var (_, admin) = await TenantAsync();

        var body = await ServedAsync(admin);

        body.GetProperty("version").GetInt32().Should().Be(1);
        body.GetProperty("blocks").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_hinted_schema_is_served_exactly()
    {
        var (_, admin) = await TenantAsync();
        await RegisterAsync(admin, """
            {
              "pricing": {
                "type": "object",
                "required": ["heading"],
                "x-label": "Pricing table",
                "x-perViewer": true,
                "properties": {
                  "heading": { "type": "string", "x-label": "Title", "x-kind": "markdown" },
                  "tone": { "enum": ["page", "accent"], "x-label": "Tone" },
                  "seats": { "type": "integer", "minimum": 1, "maximum": 50, "x-label": "Seats" },
                  "columns": { "type": "array", "items": { "type": "array" }, "minItems": 1, "maxItems": 3,
                               "x-kind": "slots", "x-label": "Columns" }
                }
              }
            }
            """);

        var served = await ServedAsync(admin);

        var expected = JsonDocument.Parse("""
            { "version": 1, "blocks": [ {
              "type": "pricing", "label": "Pricing table", "perViewer": true,
              "fields": [
                { "name": "heading", "kind": "markdown", "label": "Title", "required": true },
                { "name": "tone", "kind": "select", "label": "Tone", "required": false, "options": ["page", "accent"] },
                { "name": "seats", "kind": "number", "label": "Seats", "required": false, "min": 1, "max": 50 },
                { "name": "columns", "kind": "slots", "label": "Columns", "required": false, "min": 1, "max": 3 }
              ] } ] }
            """).RootElement;
        JsonSerializer.Serialize(served).Should().Be(JsonSerializer.Serialize(expected));
    }

    [Fact]
    public async Task An_unhinted_schema_gets_the_defaults()
    {
        var (_, admin) = await TenantAsync();
        await RegisterAsync(admin, """
            {
              "hero-banner": {
                "type": "object",
                "required": ["heading"],
                "properties": {
                  "heading": { "type": "string" },
                  "link": { "type": "string", "format": "uri" },
                  "tone": { "enum": ["page", "accent"] },
                  "maxItems": { "type": "number", "minimum": 0 },
                  "showTitle": { "type": "boolean" },
                  "extra": { "type": "object" }
                }
              }
            }
            """);

        var block = Block(await ServedAsync(admin), "hero-banner");

        block.GetProperty("label").GetString().Should().Be("Hero banner");
        block.GetProperty("perViewer").GetBoolean().Should().BeFalse();
        var fields = block.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!);
        fields.Keys.Should().Equal("heading", "link", "tone", "maxItems", "showTitle", "extra");
        fields["heading"].GetProperty("kind").GetString().Should().Be("text");
        fields["heading"].GetProperty("label").GetString().Should().Be("Heading");
        fields["heading"].GetProperty("required").GetBoolean().Should().BeTrue();
        fields["link"].GetProperty("kind").GetString().Should().Be("url");
        fields["tone"].GetProperty("kind").GetString().Should().Be("select");
        fields["tone"].GetProperty("options").EnumerateArray().Select(o => o.GetString()).Should().Equal("page", "accent");
        fields["maxItems"].GetProperty("kind").GetString().Should().Be("number");
        fields["maxItems"].GetProperty("label").GetString().Should().Be("Max items");
        fields["maxItems"].GetProperty("min").GetDouble().Should().Be(0);
        fields["maxItems"].TryGetProperty("max", out _).Should().BeFalse("it declares no maximum");
        fields["showTitle"].GetProperty("kind").GetString().Should().Be("boolean");
        fields["showTitle"].GetProperty("label").GetString().Should().Be("Show title");
        fields["extra"].GetProperty("kind").GetString().Should().Be("text");
    }

    [Fact]
    public async Task One_tenants_schema_is_not_served_to_another()
    {
        var (_, first) = await TenantAsync();
        var (_, second) = await TenantAsync();
        await RegisterAsync(first, """{ "only-first": { "type": "object" } }""");

        (await ServedAsync(second)).GetProperty("blocks").GetArrayLength().Should().Be(0);
        Block(await ServedAsync(first), "only-first").GetProperty("label").GetString().Should().Be("Only first");
    }

    [Theory]
    [InlineData("""{ "b": { "type": "object", "x-label": "" } }""", "x-label")]
    [InlineData("""{ "b": { "type": "object", "x-perViewer": "yes" } }""", "x-perViewer")]
    [InlineData("""{ "b": { "type": "object", "properties": { "p": { "type": "string", "x-kind": "dial" } } } }""", "x-kind")]
    [InlineData("""{ "b": { "type": "object", "x-other": 1 } }""", "x-other")]
    public async Task A_bad_hint_is_refused_naming_it(string blocks, string expected)
    {
        var (_, admin) = await TenantAsync();

        var res = await admin.PutAsync("/api/blocks/schema",
            new StringContent($$"""{ "blocks": {{blocks}} }""", System.Text.Encoding.UTF8, "application/json"), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain(expected);
    }

    // The reserved dataSource key

    private static async Task<string> PageTypeAsync(HttpClient admin)
    {
        var name = NewName("dspage");
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, isPubliclyDeliverable = true,
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Slug", type = "slug" },
                new { name = "Blocks", type = "blocks", editor = "blocks" },
            },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return name;
    }

    private static Task<HttpResponseMessage> WritePageAsync(HttpClient admin, string type, string blocksJson) =>
        admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status = "Draft",
            data = new Dictionary<string, object>
            {
                ["Title"] = "Home", ["Slug"] = NewName("p"), ["Blocks"] = JsonDocument.Parse(blocksJson).RootElement,
            },
        }, Ct);

    [Theory]
    [InlineData("""[ { "type": "cards", "dataSource": "entries" } ]""")]
    [InlineData("""[ { "type": "cards", "dataSource": "forms" } ]""")]
    [InlineData("""[ { "type": "cards" } ]""")]
    public async Task A_registered_data_source_or_none_saves(string blocks)
    {
        var (_, admin) = await TenantAsync();
        await RegisterAsync(admin, """{ "cards": { "type": "object", "properties": { "heading": { "type": "string" } } } }""");
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type, blocks);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_registered_data_source_saves_with_no_schema_registered()
    {
        var (_, admin) = await TenantAsync();
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type, """[ { "type": "cards", "dataSource": "entries" } ]""");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task An_unknown_data_source_is_a_400_naming_the_block_and_the_id()
    {
        var (_, admin) = await TenantAsync();
        await RegisterAsync(admin, """{ "cards": { "type": "object" } }""");
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type,
            """[ { "type": "cards", "dataSource": "entries" }, { "type": "cards", "dataSource": "nope" } ]""");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Blocks[1].dataSource 'nope' is not a registered data source");
    }

    [Fact]
    public async Task An_unknown_data_source_is_refused_with_no_schema_registered_too()
    {
        var (_, admin) = await TenantAsync();
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type, """[ { "type": "cards", "dataSource": "nope" } ]""");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Blocks[0].dataSource 'nope' is not a registered data source");
    }

    [Fact]
    public async Task A_binding_keeps_its_meaning_beside_a_data_source()
    {
        var (_, admin) = await TenantAsync();
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type, """[ { "type": "cards", "dataSource": "entries", "binding": "latest" } ]""");

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("Blocks[0].binding is the id of a binding");
    }

    [Fact]
    public async Task No_block_may_declare_a_property_named_data_source()
    {
        var (_, admin) = await TenantAsync();

        var res = await admin.PutAsync("/api/blocks/schema",
            new StringContent("""{ "blocks": { "cards": { "type": "object", "properties": { "dataSource": { "type": "string" } } } } }""",
                System.Text.Encoding.UTF8, "application/json"), Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().Contain("declares 'dataSource', which every block carries");
    }
}
