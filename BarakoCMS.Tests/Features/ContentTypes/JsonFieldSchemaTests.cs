using System.Net;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A <c>json</c> field may declare a JSON Schema its values have to match, and a write naming a
/// field the type does not declare is refused (#924).
/// </summary>
[Collection("Integration2")]
public class JsonFieldSchemaTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public JsonFieldSchemaTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NewName() => "jsonschema" + Guid.NewGuid().ToString("n")[..10];

    private static readonly object LinkList = new
    {
        type = "array",
        maxItems = 3,
        items = new
        {
            type = "object",
            required = new[] { "label", "href" },
            additionalProperties = false,
            properties = new
            {
                label = new { type = "string", minLength = 1 },
                href = new { type = "string", pattern = "^(/|https?://)" },
                variant = new { @enum = new[] { "primary", "plain" } },
            },
        },
    };

    private async Task<HttpResponseMessage> CreateTypeAsync(string name, params object[] fields) =>
        await _client.PostAsJsonAsync("/api/content-types", new { name, displayName = name, fields });

    private async Task<string> LinksTypeAsync()
    {
        var name = NewName();
        var res = await CreateTypeAsync(name,
            new { name = "Title", type = "string" },
            new { name = "Links", type = "json", jsonSchema = LinkList },
            new { name = "Extra", type = "json" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return name;
    }

    private Task<HttpResponseMessage> WriteAsync(string type, Dictionary<string, object> data) =>
        _client.PostAsJsonAsync("/api/contents", new { contentType = type, data });

    private static async Task<string> RefusedAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return body;
    }

    // ---- write time -----------------------------------------------------------------------

    [Fact]
    public async Task A_value_that_matches_the_schema_is_stored()
    {
        var type = await LinksTypeAsync();

        var res = await WriteAsync(type, new()
        {
            ["Title"] = "t",
            ["Links"] = new object[] { new { label = "Home", href = "/", variant = "plain" } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_list_of_strings_where_links_are_declared_is_refused_naming_the_path()
    {
        var type = await LinksTypeAsync();

        var body = await RefusedAsync(await WriteAsync(type, new() { ["Links"] = new[] { "Home" } }));

        body.Should().Contain("Links[0]").And.Contain("should be an object");
    }

    [Theory]
    [InlineData("""[{"label":"Home"}]""", "Links[0].href is required")]
    [InlineData("""[{"label":"","href":"/"}]""", "Links[0].label should be at least 1")]
    [InlineData("""[{"label":"Home","href":"javascript:alert(1)"}]""", "Links[0].href does not match")]
    [InlineData("""[{"label":"Home","href":"/","variant":"loud"}]""", "Links[0].variant should be one of")]
    [InlineData("""[{"label":"Home","href":"/","colour":"red"}]""", "Links[0].colour is not allowed")]
    [InlineData("""[{"label":"a","href":"/"},{"label":"b","href":"/"},{"label":"c","href":"/"},{"label":"d","href":"/"}]""", "at most 3 items")]
    [InlineData("""{"label":"Home","href":"/"}""", "Links should be an array")]
    public async Task Each_keyword_refuses_what_it_describes(string links, string expected)
    {
        var type = await LinksTypeAsync();

        var body = await RefusedAsync(await WriteAsync(type, new()
        {
            ["Links"] = System.Text.Json.JsonDocument.Parse(links).RootElement,
        }));

        body.Should().Contain(expected);
    }

    [Fact]
    public async Task Patterns_share_one_time_budget_per_value_and_a_value_past_it_is_refused()
    {
        var name = NewName();
        (await CreateTypeAsync(name, new
        {
            name = "Codes", type = "json",
            jsonSchema = new { type = "array", items = new { type = "string", pattern = "^(a+)+$" } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var slow = Enumerable.Repeat(new string('a', 40) + "!", 50).ToArray();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var body = await RefusedAsync(await WriteAsync(name, new() { ["Codes"] = slow }));
        clock.Stop();

        body.Should().Contain("could not be checked against its patterns in time");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "fifty strings must not each spend the per-match timeout");
    }

    [Fact]
    public async Task A_json_field_with_no_schema_still_takes_any_object_or_array()
    {
        var type = await LinksTypeAsync();

        var res = await WriteAsync(type, new() { ["Extra"] = new object[] { "anything", 1, new { a = true } } });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    /// <summary>Creates an entry through the API, then changes its type's definition underneath it.</summary>
    private async Task<Guid> EntryThenRedefineAsync(
        string type, Dictionary<string, object> data, Action<ContentTypeDefinition> redefine)
    {
        var created = await WriteAsync(type, data);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type);
        redefine(definition);
        session.Store(definition);
        await session.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task A_stored_value_that_fails_a_schema_added_later_does_not_block_an_edit_of_another_field()
    {
        var type = await LinksTypeAsync();
        var id = await EntryThenRedefineAsync(type,
            new() { ["Title"] = "old", ["Extra"] = new[] { "stored before the schema" } },
            d => d.Fields.Single(f => f.Name == "Extra").JsonSchema =
                System.Text.Json.JsonSerializer.SerializeToElement(LinkList));

        var kept = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Extra"] = new[] { "stored before the schema" } },
            version = 1,
        });
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());

        var changed = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Extra"] = new[] { "changed" } },
            version = 2,
        });
        (await RefusedAsync(changed)).Should().Contain("Extra[0]");
    }

    // ---- definition time ------------------------------------------------------------------

    [Theory]
    [InlineData("string", """{"type":"array"}""", "not json")]
    [InlineData("json", """{"oneOf":[{"type":"array"}]}""", "'oneOf'")]
    [InlineData("json", """{"$ref":"#/x"}""", "'$ref'")]
    [InlineData("json", """{"type":"list"}""", "names a type")]
    [InlineData("json", """{"type":"array","items":"string"}""", "is not a schema")]
    [InlineData("json", """{"type":"string","pattern":"("}""", "regular expression")]
    [InlineData("json", """{"type":"array","maxItems":-1}""", "whole number")]
    [InlineData("json", """[{"type":"array"}]""", "is not a schema")]
    public async Task A_schema_the_validator_cannot_honour_is_refused_when_the_type_is_saved(
        string fieldType, string schema, string expected)
    {
        var res = await CreateTypeAsync(NewName(), new
        {
            name = "Links", type = fieldType, jsonSchema = System.Text.Json.JsonDocument.Parse(schema).RootElement,
        });

        (await RefusedAsync(res)).Should().Contain(expected);
    }

    [Fact]
    public async Task A_schema_nested_past_the_depth_limit_is_refused()
    {
        var schema = "true";
        for (var i = 0; i < 20; i++)
        {
            schema = $$"""{"type":"array","items":{{schema}}}""";
        }

        var res = await CreateTypeAsync(NewName(), new
        {
            name = "Links", type = "json", jsonSchema = System.Text.Json.JsonDocument.Parse(schema).RootElement,
        });

        (await RefusedAsync(res)).Should().Contain("deeper than");
    }

    [Fact]
    public async Task Describe_lists_the_keywords_a_schema_may_use()
    {
        var res = await _client.GetAsync("/api/meta/describe");
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var keywords = body.GetProperty("jsonSchemaKeywords").EnumerateArray().Select(k => k.GetString()).ToList();
        keywords.Should().NotBeEmpty();
        keywords.Should().Contain(["type", "properties", "items", "required", "enum", "pattern"]);
        keywords.Should().NotContain("$ref");
        body.GetProperty("arrayItemTypes").EnumerateArray().Select(k => k.GetString()).Should().Contain("url");
    }

    // ---- undeclared fields ----------------------------------------------------------------

    [Fact]
    public async Task A_create_naming_a_field_the_type_does_not_declare_is_refused_naming_it()
    {
        var type = await LinksTypeAsync();

        var body = await RefusedAsync(await WriteAsync(type, new() { ["Title"] = "t", ["Nickname"] = "x" }));

        body.Should().Contain("'Nickname'").And.Contain("does not declare");
    }

    [Fact]
    public async Task Field_names_are_matched_ignoring_case_as_every_other_reader_does()
    {
        var type = await LinksTypeAsync();

        var res = await WriteAsync(type, new() { ["title"] = "t" });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_update_may_keep_an_undeclared_key_the_entry_already_holds_and_may_not_add_one()
    {
        var name = NewName();
        (await CreateTypeAsync(name,
            new { name = "Title", type = "string" },
            new { name = "Legacy", type = "string" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await EntryThenRedefineAsync(name,
            new() { ["Title"] = "old", ["Legacy"] = "from a removed field" },
            d => d.Fields.RemoveAll(f => f.Name == "Legacy"));

        var kept = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Legacy"] = "from a removed field" },
            version = 1,
        });
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());

        var added = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Title"] = "new", ["Legacy"] = "changed", ["Other"] = 1 },
            version = 2,
        });
        var body = await RefusedAsync(added);
        body.Should().Contain("'Legacy'").And.Contain("'Other'");
    }

    [Fact]
    public async Task A_type_with_no_definition_still_takes_any_key()
    {
        var res = await WriteAsync(NewName(), new() { ["Anything"] = "goes" });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }
}
