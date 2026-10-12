using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using BarakoCMS.Tests.Features.Tenants;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Site;

/// <summary>
/// The site blueprint declares a schema for its json fields and makes <c>Mode</c> a choice of
/// <c>Live</c> and <c>Holding</c>, and <c>migrations/5.0.0/site-field-schemas.sql</c> brings a site
/// type applied before 5.0.0 up to it (#924).
/// </summary>
[Collection("Sequential")]
public class SiteFieldSchemasTests
{
    private const string Up = "site-field-schemas.sql";
    private const string Down = "rollback-site-field-schemas.sql";

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
            schemas[field!["name"]!.GetValue<string>()] = field["jsonSchema"]!.DeepClone();
        schemas.Count.Should().BeGreaterThan(10);

        var sql = File.ReadAllText(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", Up));
        var literal = Regex.Match(sql, @"schemas\s+constant jsonb := '(.*?)'::jsonb;").Groups[1].Value;
        literal.Should().NotBeEmpty();

        JsonNode.DeepEquals(JsonNode.Parse(literal), schemas).Should().BeTrue(
            "the migration gives an existing site type the schemas a new one gets from the blueprint");

        var mode = fields.Single(f => f!["name"]!.GetValue<string>() == "Mode")!;
        var options = Regex.Match(sql, @"mode_options\s+constant jsonb := '(.*?)'::jsonb;").Groups[1].Value;
        var expected = new JsonArray(mode["options"]!.AsArray()
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
        type.Fields.Where(f => f.JsonSchema is not null).Should().BeEmpty();
        type.Fields.Should().HaveCountGreaterThan(3);
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

    /// <summary>A site type as a tenant applied it before 5.0.0, plus a json field with its own schema.</summary>
    private Task OlderSiteTypeAsync(string slug) =>
        TenantProfileSeed.StoreSiteTypeAsync(_fixture, slug, Ct,
            TenantProfileSeed.Declared("HeaderLinks", "json"),
            TenantProfileSeed.Declared("Colors", "json"),
            TenantProfileSeed.Declared("Presets", "json"),
            TenantProfileSeed.Declared("Mode", "string"),
            new FieldDefinition
            {
                Name = "Own", DisplayName = "Own", Type = "json",
                JsonSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement,
            });

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
        var sql = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "migrations", "5.0.0", file), Ct);
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

        if (file == Up)
            notices.Should().Contain(n => n.Contains($"Only {slug} was looked at"));
        return notices;
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test binary should sit under the repository");
        return directory!.FullName;
    }
}
