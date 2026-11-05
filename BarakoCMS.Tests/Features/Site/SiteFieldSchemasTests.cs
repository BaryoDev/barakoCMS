using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// The site blueprint declares a schema for its json fields and makes <c>Mode</c> a choice of
/// <c>Live</c> and <c>Holding</c>, and <c>migrations/5.0.0/site-field-schemas.sql</c> brings a site
/// type applied before 5.0.0 up to it (#924). <c>migrations/5.5.0/site-fonts-schema.sql</c> then
/// moves <c>Fonts</c> to the schema that also takes a font object.
/// </summary>
[Collection("Integration1")]
public class SiteFieldSchemasTests
{
    private const string Up = "site-field-schemas.sql";
    private const string Down = "rollback-site-field-schemas.sql";
    private const string FontsUp = "site-fonts-schema.sql";
    private const string FontsDown = "rollback-site-fonts-schema.sql";

    private readonly IntegrationTestFixture _fixture;

    public SiteFieldSchemasTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> BlueprintTenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        await TenantProfileSeed.ApplySiteBlueprintAsync(admin, Ct);
        return admin;
    }

    private static Task<HttpResponseMessage> WriteSiteAsync(HttpClient admin, Dictionary<string, object> data) =>
        admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, Ct);

    // ---- the blueprint --------------------------------------------------------------------

    [Fact]
    public async Task Header_links_written_as_a_list_of_strings_are_refused_naming_the_path()
    {
        var admin = await BlueprintTenantAsync();

        var res = await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["HeaderLinks"] = new[] { "Donate" } });

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("HeaderLinks[0]");
    }

    [Fact]
    public async Task The_shapes_docs_site_settings_shows_are_accepted()
    {
        var admin = await BlueprintTenantAsync();

        var res = await WriteSiteAsync(admin, new()
        {
            ["Name"] = "Club",
            ["Mode"] = "Holding",
            ["Colors"] = Parse("""{ "accent": "#17458F", "ink": "#1C1C1C" }"""),
            ["Collections"] = Parse("""{ "events": { "type": "event", "route": "/events", "fields": { "title": "Title" }, "sort": "-StartDate", "noun": ["event", "events"] } }"""),
            ["OptionStyles"] = Parse("""{ "project.AreaOfFocus": { "Water": { "tone": "sky", "icon": "location", "label": "Water" } } }"""),
            ["Variants"] = Parse("""[ { "name": "pine", "label": "Pine", "colors": { "accent": "#1A6B41" } } ]"""),
            ["TopBar"] = Parse("""{ "text": "City", "links": [ { "label": "Facebook", "href": "https://facebook.com/x" } ] }"""),
            ["HeaderLinks"] = Parse("""[ { "label": "Docs", "href": "/docs", "activeOn": "/docs /guides", "children": [ { "label": "API", "href": "/docs/api" } ] } ]"""),
            ["FooterColumns"] = Parse("""[ { "heading": "Club", "links": [ { "label": "About", "href": "/about" } ] } ]"""),
            ["SocialLinks"] = Parse("""[ { "network": "facebook", "href": "https://facebook.com/x" } ]"""),
            ["HeaderActions"] = Parse("""[ { "label": "Donate", "href": "/donate", "variant": "primary" } ]"""),
            ["Tones"] = Parse("""{ "cms": { "ink": "cms-ink", "bg": "cms-bg", "edge": "#B9C8F5" } }"""),
            ["StyleRecipes"] = Parse("""{ "card": { "class": "lift", "style": { "padding": "22px 24px" } } }"""),
            ["Plugins"] = Parse("""[ "tally" ]"""),
            ["Labels"] = Parse("""{ "minRead": "minutong pagbasa" }"""),
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("Holdng")]
    [InlineData("live")]
    public async Task A_mode_other_than_live_or_holding_is_refused(string mode)
    {
        var admin = await BlueprintTenantAsync();

        var res = await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Mode"] = mode });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Holding")]
    public async Task Live_and_holding_are_accepted(string mode)
    {
        var admin = await BlueprintTenantAsync();

        var res = await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Mode"] = mode });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_field_the_blueprint_does_not_declare_is_refused()
    {
        var admin = await BlueprintTenantAsync();

        var res = await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Theme"] = "dark" });

        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("'Theme'");
    }

    // ---- the migration --------------------------------------------------------------------

    [Fact]
    public void The_migration_carries_the_blueprints_schemas_and_mode_options_exactly()
    {
        var blueprint = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "barakoCMS", "Blueprints", "site.json")))!;
        var fields = blueprint["contentTypes"]![0]!["fields"]!.AsArray();
        var schemas = new JsonObject();
        foreach (var field in fields.Where(f => f!["jsonSchema"] is not null))
        {
            schemas[field!["name"]!.GetValue<string>()] = field["jsonSchema"]!.DeepClone();
        }

        schemas.Count.Should().BeGreaterThan(10);

        var sql = File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", Up));
        var literal = Regex.Match(sql, @"schemas\s+constant jsonb := '(.*?)'::jsonb;").Groups[1].Value;
        literal.Should().NotBeEmpty();

        // 5.0.0 shipped before Fonts took a font object; 5.5.0 site-fonts-schema.sql patches it.
        var shipped = JsonNode.Parse(literal)!.AsObject();
        var fontsSql = File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.5.0", FontsUp));
        JsonNode.DeepEquals(shipped["Fonts"], JsonNode.Parse(FontsSchema(fontsSql, "old_schema"))).Should().BeTrue(
            "5.5.0 only replaces the Fonts schema 5.0.0 set");
        shipped["Fonts"] = JsonNode.Parse(FontsSchema(fontsSql, "new_schema"));

        JsonNode.DeepEquals(shipped, schemas).Should().BeTrue(
            "the migrations give an existing site type the schemas a new one gets from the blueprint");

        var mode = fields.Single(f => f!["name"]!.GetValue<string>() == "Mode")!;
        var options = Regex.Match(sql, @"mode_options\s+constant jsonb := '(.*?)'::jsonb;").Groups[1].Value;
        // 5.0.0 shipped before Private (#858); a type created earlier gains Private through set-field-options.
        var expected = new JsonArray(mode["options"]!.AsArray()
            .Where(o => o!["value"]!.GetValue<string>() != "Private")
            .Select(o => (JsonNode)new JsonObject { ["Value"] = o!["value"]!.DeepClone(), ["Label"] = o["label"]!.DeepClone() })
            .ToArray());
        JsonNode.DeepEquals(JsonNode.Parse(options), expected).Should().BeTrue();
    }

    [Fact]
    public async Task The_migration_gives_an_older_site_type_the_schemas_and_mode_choice_and_its_rollback_takes_them_away()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await OlderSiteTypeAsync(slug);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);

        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["HeaderLinks"] = new[] { "Donate" }, ["Mode"] = "Holdng" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "nothing is checked before the migration");
        await EraseSiteEntriesAsync(slug);

        await RunAsync(Up, slug);

        var type = await SiteTypeAsync(slug);
        Field(type, "HeaderLinks").JsonSchema.Should().NotBeNull();
        Field(type, "Colors").JsonSchema.Should().NotBeNull();
        Field(type, "Presets").JsonSchema.Should().BeNull("the blueprint declares no schema for saved blocks");
        JsonNode.DeepEquals(JsonNode.Parse(Field(type, "Own").JsonSchema!.Value.GetRawText()), JsonNode.Parse("""{"type":"object"}"""))
            .Should().BeTrue("a schema someone declared is kept");
        Field(type, "Mode").Type.Should().Be("choice");
        Field(type, "Mode").Options!.Select(o => o.Value).Should().Equal("Live", "Holding");

        var refused = await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["HeaderLinks"] = new[] { "Donate" } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Mode"] = "Holdng" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var version = await TypeVersionAsync(slug);
        await RunAsync(Up, slug);
        (await TypeVersionAsync(slug)).Should().Be(version, "a second run finds nothing to change");

        await RunAsync(Down, slug);

        type = await SiteTypeAsync(slug);
        type.Fields.Select(f => f.Name).Should().Contain(["HeaderLinks", "Colors", "Own"]);
        type.Fields.Where(f => f.JsonSchema is not null).Should().BeEmpty();
        Field(type, "Mode").Type.Should().Be("string");
    }

    [Fact]
    public async Task An_entry_holding_another_mode_keeps_mode_a_string_and_is_named()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await OlderSiteTypeAsync(slug);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        await TenantProfileSeed.CreateSiteEntryAsync(admin, new() { ["Name"] = "Club", ["Mode"] = "Maintenance" }, Ct);

        var notices = await RunAsync(Up, slug);

        notices.Should().Contain(n => n.Contains(slug) && n.Contains("keeps Mode as a string"));
        var type = await SiteTypeAsync(slug);
        Field(type, "Mode").Type.Should().Be("string");
        Field(type, "HeaderLinks").JsonSchema.Should().NotBeNull("the schemas do not wait on Mode");
    }

    [Fact]
    public async Task The_fonts_migration_moves_an_untouched_fonts_schema_to_the_blueprints_and_its_rollback_moves_it_back()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        await OlderSiteTypeAsync(slug, FieldWithFonts());
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct);
        var font = Parse("""{ "heading": { "family": "Inter", "url": "https://example.com/inter.css", "fallback": "sans-serif" } }""");

        await RunAsync(Up, slug);
        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Fonts"] = font }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "5.0.0 accepts a family name only");

        await RunAsync(FontsUp, slug);

        var blueprint = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "barakoCMS", "Blueprints", "site.json")))!;
        var expected = blueprint["contentTypes"]![0]!["fields"]!.AsArray()
            .Single(f => f!["name"]!.GetValue<string>() == "Fonts")!["jsonSchema"]!;
        JsonNode.DeepEquals(JsonNode.Parse(Field(await SiteTypeAsync(slug), "Fonts").JsonSchema!.Value.GetRawText()), expected)
            .Should().BeTrue("an existing site type ends up with the schema a new one gets");
        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Fonts"] = font }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Fonts"] = Parse("""{ "heading": "Inter" }""") }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await WriteSiteAsync(admin, new() { ["Name"] = "Club", ["Fonts"] = Parse("""{ "heading": { "url": "https://example.com/a.css" } }""") }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "family is required in a font object");

        var version = await TypeVersionAsync(slug);
        await RunAsync(FontsUp, slug);
        (await TypeVersionAsync(slug)).Should().Be(version, "a second run finds nothing to change");

        await RunAsync(FontsDown, slug);
        JsonNode.DeepEquals(
                JsonNode.Parse(Field(await SiteTypeAsync(slug), "Fonts").JsonSchema!.Value.GetRawText()),
                JsonNode.Parse("""{"type":"object","additionalProperties":{"type":"string"}}"""))
            .Should().BeTrue();
    }

    [Fact]
    public async Task The_fonts_migration_leaves_a_fonts_schema_a_tenant_changed_alone()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var custom = """{"type":"object","maxProperties":2,"additionalProperties":{"type":"string"}}""";
        await OlderSiteTypeAsync(slug, new FieldDefinition
        {
            Name = "Fonts", DisplayName = "Fonts", Type = "json", JsonSchema = JsonDocument.Parse(custom).RootElement,
        });
        await RunAsync(Up, slug);
        var version = await TypeVersionAsync(slug);

        await RunAsync(FontsUp, slug);

        JsonNode.DeepEquals(JsonNode.Parse(Field(await SiteTypeAsync(slug), "Fonts").JsonSchema!.Value.GetRawText()), JsonNode.Parse(custom))
            .Should().BeTrue();
        (await TypeVersionAsync(slug)).Should().Be(version);

        await RunAsync(FontsDown, slug);
        JsonNode.DeepEquals(JsonNode.Parse(Field(await SiteTypeAsync(slug), "Fonts").JsonSchema!.Value.GetRawText()), JsonNode.Parse(custom))
            .Should().BeTrue();
    }

    private static string FontsSchema(string sql, string name) =>
        Regex.Match(sql, name + @"\s+constant jsonb := '(.*?)'::jsonb;").Groups[1].Value;

    /// <summary>A Fonts field as 5.0.0's migration leaves it when the type declared none.</summary>
    private static FieldDefinition FieldWithFonts() => TenantProfileSeed.Declared("Fonts", "json");

    /// <summary>A site type as a tenant applied it before 5.0.0, plus a json field with its own schema.</summary>
    private Task OlderSiteTypeAsync(string slug, params FieldDefinition[] more) =>
        TenantProfileSeed.StoreSiteTypeAsync(_fixture, slug, Ct,
            [
            ..more,
            TenantProfileSeed.Declared("HeaderLinks", "json"),
            TenantProfileSeed.Declared("Colors", "json"),
            TenantProfileSeed.Declared("Presets", "json"),
            TenantProfileSeed.Declared("Mode", "string"),
            new FieldDefinition
            {
                Name = "Own", DisplayName = "Own", Type = "json",
                JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            }]);

    private async Task EraseSiteEntriesAsync(string slug)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(slug);
        session.DeleteWhere<Content>(c => c.ContentType == "site");
        await session.SaveChangesAsync(Ct);
    }

    private async Task<ContentTypeDefinition> SiteTypeAsync(string slug)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(slug);
        return await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == "site", Ct);
    }

    private async Task<Guid> TypeVersionAsync(string slug)
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select mt_version from public.mt_doc_contenttypedefinition where tenant_id = @t and data ->> 'Name' = 'site'",
            connection);
        command.Parameters.AddWithValue("t", slug);
        return (Guid)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static FieldDefinition Field(ContentTypeDefinition type, string name) =>
        type.Fields.Single(f => f.Name == name);

    private async Task<List<string>> RunAsync(string file, string slug)
    {
        var version = file.Contains("fonts") ? "5.5.0" : "5.0.0";
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", version, file), Ct);
        var notices = new List<string>();

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        connection.Notice += (_, e) => notices.Add(e.Notice.MessageText);
        await connection.OpenAsync(Ct);

        await using (var scope = new NpgsqlCommand("select set_config('barako.only_tenant', @slug, false)", connection))
        {
            scope.Parameters.AddWithValue("slug", slug);
            await scope.ExecuteNonQueryAsync(Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);

        if (file is Up or FontsUp)
        {
            notices.Should().Contain(n => n.Contains($"Only {slug} was looked at"));
        }

        return notices;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
