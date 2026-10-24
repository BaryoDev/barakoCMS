using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The <c>blocks</c> field type, the per-tenant block schema a renderer registers, presets and
/// bindings as tenant data, and page resolve returning a block's bound entries (#926).
/// </summary>
[Collection("IntegrationSerial")]
public class BlocksFieldTests
{
    private const string RendererKey = "renderer-key-for-the-blocks-tests-0123456789";

    private readonly IntegrationTestFixture _fixture;

    public BlocksFieldTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    private static readonly object HeroAndCards = new
    {
        blocks = new Dictionary<string, object>
        {
            ["hero"] = new
            {
                type = "object",
                required = new[] { "heading" },
                properties = new { heading = new { type = "string", minLength = 1 }, tone = new { @enum = new[] { "page", "accent" } } },
            },
            ["cards"] = new { type = "object", properties = new { heading = new { type = "string" } } },
        },
    };

    private async Task<(string Slug, HttpClient Admin)> TenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct));
    }

    private static async Task<string> PageTypeAsync(HttpClient admin)
    {
        var name = NewName("blockpage");
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

    private static Task<HttpResponseMessage> WritePageAsync(HttpClient admin, string type, string blocksJson, string status = "Draft") =>
        admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            status,
            data = new Dictionary<string, object>
            {
                ["Title"] = "Home", ["Slug"] = NewName("p"), ["Blocks"] = JsonDocument.Parse(blocksJson).RootElement,
            },
        }, Ct);

    private static async Task<string> RefusedAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return body;
    }

    // ---- slice 1: the field type and the schema -------------------------------------------

    [Fact]
    public async Task Two_tenants_registering_different_schemas_each_read_their_own()
    {
        var (_, first) = await TenantAsync();
        var (_, second) = await TenantAsync();

        (await first.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.PutAsJsonAsync("/api/blocks/schema", new
        {
            blocks = new Dictionary<string, object> { ["tally"] = new { type = "object" } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var a = await (await first.GetAsync("/api/blocks/schema", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var b = await (await second.GetAsync("/api/blocks/schema", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        a.GetProperty("blocks").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["hero", "cards"]);
        b.GetProperty("blocks").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["tally"]);
    }

    [Fact]
    public async Task A_tenant_with_no_schema_answers_not_found_so_a_console_falls_back()
    {
        var (_, admin) = await TenantAsync();

        (await admin.GetAsync("/api/blocks/schema", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_page_block_with_a_property_its_block_does_not_declare_is_refused_naming_it()
    {
        var (_, admin) = await TenantAsync();
        await admin.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct);
        var type = await PageTypeAsync(admin);

        var body = await RefusedAsync(await WritePageAsync(admin, type,
            """[ { "type": "hero", "heading": "Hi" }, { "type": "hero", "heading": "Hi", "colour": "red" } ]"""));

        body.Should().Contain("Blocks[1].colour is not a property of 'hero'");
    }

    [Theory]
    [InlineData("""[ { "type": "carousel" } ]""", "'carousel' is not a block")]
    [InlineData("""[ { "type": "hero" } ]""", "Blocks[0].heading is required")]
    [InlineData("""[ { "type": "hero", "heading": "Hi", "tone": "loud" } ]""", "Blocks[0].tone should be one of")]
    [InlineData("""[ { "type": "hero", "heading": "Hi", "binding": "latest" } ]""", "Blocks[0].binding is the id of a binding")]
    public async Task A_page_block_the_schema_refuses_is_a_400_naming_the_path(string blocks, string expected)
    {
        var (_, admin) = await TenantAsync();
        await admin.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct);
        var type = await PageTypeAsync(admin);

        (await RefusedAsync(await WritePageAsync(admin, type, blocks))).Should().Contain(expected);
    }

    [Fact]
    public async Task A_block_that_declares_additional_properties_takes_them_as_its_schema_says()
    {
        var (_, admin) = await TenantAsync();
        (await admin.PutAsJsonAsync("/api/blocks/schema", new
        {
            blocks = new Dictionary<string, object> { ["kv"] = new { type = "object", additionalProperties = new { type = "string" } } },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var type = await PageTypeAsync(admin);

        (await WritePageAsync(admin, type, """[ { "type": "kv", "a": "x", "b": "y" } ]""")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefusedAsync(await WritePageAsync(admin, type, """[ { "type": "kv", "a": 1 } ]"""))).Should().Contain("Blocks[0].a should be a string");
    }

    [Fact]
    public async Task Blocks_the_schema_allows_are_stored()
    {
        var (_, admin) = await TenantAsync();
        await admin.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct);
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type,
            $$"""[ { "type": "hero", "id": "b1", "heading": "Hi", "tone": "accent" }, { "type": "cards", "binding": "{{Guid.NewGuid()}}" } ]""");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("""[ { "type": "anything", "whatever": 1 } ]""", true)]
    [InlineData("""[ "hero" ]""", false)]
    [InlineData("""[ { "heading": "no type" } ]""", false)]
    [InlineData("""{ "type": "hero" }""", false)]
    public async Task With_no_schema_only_the_shape_is_checked(string blocks, bool accepted)
    {
        var (_, admin) = await TenantAsync();
        var type = await PageTypeAsync(admin);

        var res = await WritePageAsync(admin, type, blocks);

        res.StatusCode.Should().Be(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("""{ "blocks": {} }""", "at least one block")]
    [InlineData("""{ "blocks": { "1hero": { "type": "object" } } }""", "starts with a letter")]
    [InlineData("""{ "blocks": { "hero": { "oneOf": [] } } }""", "'oneOf'")]
    [InlineData("""{ "blocks": { "hero": { "properties": { "type": { "type": "string" } } } } }""", "every block carries")]
    public async Task A_schema_that_cannot_be_checked_against_is_refused(string json, string expected)
    {
        var (_, admin) = await TenantAsync();

        var res = await admin.PutAsync("/api/blocks/schema", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), Ct);

        (await RefusedAsync(res)).Should().Contain(expected);
    }

    [Fact]
    public async Task The_renderer_registers_with_its_key_and_nothing_else()
    {
        using var resolver = FastEndpointsResolver.Keep();
        var host = _fixture.WithSetting("RateLimiting:Renderer:Key", RendererKey);
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);

        HttpClient Renderer(string? key)
        {
            var client = host.CreateClient();
            client.DefaultRequestHeaders.Add("X-Tenant", slug);
            if (key is not null)
            {
                client.DefaultRequestHeaders.Add("X-Barako-Renderer-Key", key);
            }

            return client;
        }

        (await Renderer(RendererKey).PutAsJsonAsync("/api/renderer/blocks/schema", HeroAndCards, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        (await Renderer(null).PutAsJsonAsync("/api/renderer/blocks/schema", HeroAndCards, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Renderer(RendererKey + "x").PutAsJsonAsync("/api/renderer/blocks/schema", HeroAndCards, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Renderer("wrong").PutAsync("/api/renderer/blocks/schema",
                new StringContent("""{ "blocks": {} }""", System.Text.Encoding.UTF8, "application/json"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a caller without the key learns nothing from the validator");

        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var read = await (await admin.GetAsync("/api/blocks/schema", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        read.GetProperty("registeredBy").GetString().Should().Be("renderer");
    }

    [Fact]
    public async Task With_no_renderer_key_configured_the_renderer_route_is_not_there()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var client = TenantProfileSeed.Anonymous(_fixture, slug);
        client.DefaultRequestHeaders.Add("X-Barako-Renderer-Key", "");

        (await client.PutAsJsonAsync("/api/renderer/blocks/schema", HeroAndCards, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Registering_needs_manage_content_types_and_reading_needs_a_sign_in()
    {
        var (slug, admin) = await TenantAsync();
        (await admin.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var anonymous = TenantProfileSeed.Anonymous(_fixture, slug);
        (await anonymous.GetAsync("/api/blocks/schema", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        (await user.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var route in new[] { "/api/blocks/presets/" + Guid.NewGuid(), "/api/blocks/bindings/" + Guid.NewGuid() })
        {
            (await user.PutAsJsonAsync(route, new { name = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await user.DeleteAsync(route, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    // ---- slice 2: presets and bindings ----------------------------------------------------

    [Fact]
    public async Task A_preset_is_checked_against_its_block_and_listed_back()
    {
        var (_, admin) = await TenantAsync();
        await admin.PutAsJsonAsync("/api/blocks/schema", HeroAndCards, Ct);
        var id = Guid.NewGuid();

        var refused = await admin.PutAsJsonAsync($"/api/blocks/presets/{id}", new
        {
            name = "Hero", blockType = "hero", props = new { heading = "Hi", colour = "red" },
        }, Ct);
        (await RefusedAsync(refused)).Should().Contain("props.colour is not a property of 'hero'");

        var saved = await admin.PutAsJsonAsync($"/api/blocks/presets/{id}", new
        {
            name = "Hero", blockType = "hero", props = new { heading = "Hi" },
        }, Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));

        var list = await (await admin.GetAsync("/api/blocks/presets", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(1);
        items[0].GetProperty("id").GetGuid().Should().Be(id);
        items[0].GetProperty("props").GetProperty("heading").GetString().Should().Be("Hi");

        (await admin.DeleteAsync($"/api/blocks/presets/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"/api/blocks/presets/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_binding_names_a_type_and_its_fields_and_stays_in_its_tenant()
    {
        var (_, admin) = await TenantAsync();
        var (_, other) = await TenantAsync();
        var type = await PageTypeAsync(admin);
        var id = Guid.NewGuid();

        (await RefusedAsync(await admin.PutAsJsonAsync($"/api/blocks/bindings/{id}", new { name = "x", contentType = "nosuchtype" }, Ct)))
            .Should().Contain("no content type");
        (await RefusedAsync(await admin.PutAsJsonAsync($"/api/blocks/bindings/{id}", new { name = "x", contentType = type, sort = "-Nope" }, Ct)))
            .Should().Contain("no field 'Nope'");
        (await RefusedAsync(await admin.PutAsJsonAsync($"/api/blocks/bindings/{id}", new { name = "x", contentType = type, limit = 21 }, Ct)))
            .Should().Contain("limit");

        var saved = await admin.PutAsJsonAsync($"/api/blocks/bindings/{id}", new
        {
            name = "Latest", contentType = type, sort = "-Title", limit = 2, where = new Dictionary<string, string> { ["Title"] = "Home" },
        }, Ct);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));

        var mine = await (await admin.GetAsync("/api/blocks/bindings", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        mine.GetProperty("items").GetArrayLength().Should().Be(1);
        var theirs = await (await other.GetAsync("/api/blocks/bindings", Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        theirs.GetProperty("totalItems").GetInt32().Should().Be(0);
        (await other.DeleteAsync($"/api/blocks/bindings/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- slice 3: resolve materialises bindings -------------------------------------------

    [Fact]
    public async Task Resolve_returns_a_blocks_bound_entries_in_the_same_response()
    {
        var (slug, admin) = await TenantAsync();
        var posts = NewName("blockpost");
        (await admin.PostAsJsonAsync("/api/content-types", new
        {
            name = posts, displayName = posts, isPubliclyDeliverable = true,
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Rank", type = "int" },
                new { name = "Secret", type = "string", sensitivity = "Hidden" },
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        // Ranks out of step with creation order, so newest-first and highest-rank-first differ.
        foreach (var (title, rank, status) in new[] { ("one", 2, "Published"), ("two", 3, "Published"), ("three", 1, "Published"), ("draft", 9, "Draft") })
        {
            (await admin.PostAsJsonAsync("/api/contents", new
            {
                contentType = posts, status, data = new Dictionary<string, object> { ["Title"] = title, ["Rank"] = rank, ["Secret"] = "s" },
            }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var page = await PageTypeAsync(admin);
        (await admin.PutAsJsonAsync("/api/pages/settings", new { contentType = page }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var top = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var only = Guid.NewGuid();
        (await admin.PutAsJsonAsync($"/api/blocks/bindings/{top}", new { name = "Top two", contentType = posts, sort = "-Rank", limit = 2 }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"/api/blocks/bindings/{hidden}", new { name = "By secret", contentType = posts, sort = "Secret" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync($"/api/blocks/bindings/{only}", new
        {
            name = "Just one", contentType = posts, where = new Dictionary<string, string> { ["Title"] = "ONE" },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.PostAsJsonAsync("/api/contents", new
        {
            contentType = page, status = "Published",
            data = new Dictionary<string, object>
            {
                ["Title"] = "Home", ["Slug"] = "home",
                ["Blocks"] = JsonDocument.Parse($$"""[ { "type": "hero" }, { "type": "cards", "binding": "{{top}}" }, { "type": "cards", "binding": "{{hidden}}" }, { "type": "cards", "binding": "{{only}}" } ]""").RootElement,
            },
        }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await TenantProfileSeed.Anonymous(_fixture, slug).GetAsync("/api/public/pages/resolve?path=/", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var bound = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("boundBlocks").EnumerateArray().ToList();

        bound.Should().HaveCount(3);
        bound[0].GetProperty("index").GetInt32().Should().Be(1);
        bound[0].GetProperty("field").GetString().Should().Be("Blocks");
        var titles = bound[0].GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("data").GetProperty("Title").GetString()).ToList();
        titles.Should().Equal("two", "one");
        bound[0].GetProperty("entries")[0].GetProperty("data").TryGetProperty("Secret", out _).Should().BeFalse("only Public fields are delivered");

        bound[1].GetProperty("entries").GetArrayLength().Should().Be(0, "a binding ordered by a field no reader may see selects nothing");

        var matched = bound[2].GetProperty("entries").EnumerateArray().ToList();
        matched.Should().HaveCount(1);
        matched[0].GetProperty("data").GetProperty("Title").GetString().Should().Be("one", "a condition matches ignoring case");
    }
}
