using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// Importing a bundle that carries both the content types and the content is the case import exists
/// for: seeding a new site from an export. It is also the case that was broken, because everything a
/// record needs in order to be found is decided by the type it arrives alongside.
/// </summary>
/// <remarks>
/// An import into an already-configured site works, which is why this survives casual testing.
/// See issue #168.
/// </remarks>
[Collection("Sequential")]
public class ImportTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly HttpClient _anonymous;

    public ImportTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", fixture.CreateToken(roles: new[] { "SuperAdmin", "Admin" }));

        _anonymous = fixture.CreateClient();
    }

    private static object Bundle(string type, bool deliverable, string title) => new
    {
        contentTypes = new[]
        {
            new
            {
                name = type,
                displayName = type,
                description = "imported",
                isPubliclyDeliverable = deliverable,
                fields = new[]
                {
                    new { name = "Title", displayName = "Title", type = "string" },
                },
            },
        },
        contents = new[]
        {
            new
            {
                contentType = type,
                status = "Published",
                data = new Dictionary<string, object> { ["Title"] = title },
            },
        },
    };

    /// <summary>
    /// A record whose type is created by the same bundle is findable through public search
    /// afterward, which is the end-to-end statement of "the record and its schema arrived together
    /// and the import understood that".
    /// </summary>
    [Fact]
    public async Task A_record_imported_beside_its_own_new_type_is_publicly_searchable()
    {
        var type = "imported" + Guid.NewGuid().ToString("n")[..8];

        var response = await _client.PostAsJsonAsync(
            "/api/portability/import", Bundle(type, deliverable: true, title: "Findable Kumquat"));

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());

        var search = await _anonymous.GetAsync($"/api/public/{type}/search?q=kumquat");

        search.StatusCode.Should().Be(HttpStatusCode.OK,
            "an imported type that the bundle says is publicly deliverable has to be");

        using var document = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("count").GetInt32().Should().Be(1,
            "the record's search text is built from the fields of the type imported alongside it");
    }

    /// <summary>
    /// The positive control. An import that made everything publicly deliverable would pass the test
    /// above and would be a considerably worse bug than the one it fixes.
    /// </summary>
    [Fact]
    public async Task A_record_whose_type_is_not_deliverable_stays_off_the_public_api()
    {
        var type = "private" + Guid.NewGuid().ToString("n")[..8];

        var response = await _client.PostAsJsonAsync(
            "/api/portability/import", Bundle(type, deliverable: false, title: "Hidden Kumquat"));
        response.IsSuccessStatusCode.Should().BeTrue();

        (await _anonymous.GetAsync($"/api/public/{type}/search?q=kumquat")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A record whose type is nowhere to be found is still imported, but the report says so rather
    /// than leaving it to be discovered as content that never appears in search.
    /// </summary>
    [Fact]
    public async Task A_record_with_no_content_type_anywhere_is_reported()
    {
        var missing = "nosuchtype" + Guid.NewGuid().ToString("n")[..8];

        var response = await _client.PostAsJsonAsync("/api/portability/import", new
        {
            contentTypes = Array.Empty<object>(),
            contents = new[]
            {
                new
                {
                    contentType = missing,
                    status = "Published",
                    data = new Dictionary<string, object> { ["Title"] = "orphan" },
                },
            },
        });

        response.IsSuccessStatusCode.Should().BeTrue();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("contentsWithoutContentType").GetInt32().Should().Be(1);
        document.RootElement.GetProperty("unknownContentTypes").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain(missing);
    }

    /// <summary>
    /// And a dry run reports the same thing, which it could not do while the type lookup was a
    /// snapshot the dry run never added to.
    /// </summary>
    [Fact]
    public async Task A_dry_run_does_not_report_a_type_the_bundle_itself_creates_as_unknown()
    {
        var type = "dryrun" + Guid.NewGuid().ToString("n")[..8];

        var bundle = Bundle(type, deliverable: true, title: "Preview Kumquat");
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bundle));

        var payload = new Dictionary<string, object>
        {
            ["dryRun"] = true,
            ["contentTypes"] = body.GetProperty("contentTypes"),
            ["contents"] = body.GetProperty("contents"),
        };

        var response = await _client.PostAsJsonAsync("/api/portability/import", payload);
        response.IsSuccessStatusCode.Should().BeTrue();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("contentsWithoutContentType").GetInt32().Should().Be(0,
            "the bundle carries the type, so a preview must not report the record as orphaned");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == type))
            .Should().BeFalse("a dry run writes nothing");
    }

    /// <summary>
    /// An imported content type is stored under the same normalized name a created one would get.
    /// </summary>
    /// <remarks>
    /// The create endpoint slugified the name to lowercase and the importer stored the file's own
    /// spelling, while the unique index is on the raw value.
    ///
    /// The sequential case was never broken: the importer looks up an existing type with
    /// OrdinalIgnoreCase, so importing "Article" over a created "article" matches and updates. What
    /// the raw name left open is the race, where two concurrent imports both miss the lookup and the
    /// index is the only thing left, and it considered "Article" and "article" different rows.
    ///
    /// Racing two imports is not something this suite can do deterministically, so this asserts the
    /// property that closes it: what gets written is normalized, so the index compares like with
    /// like. Reverting the importer fails this on the stored value.
    /// </remarks>
    [Fact]
    public async Task An_imported_type_is_stored_under_its_normalized_name()
    {
        var lower = "casing" + Guid.NewGuid().ToString("n")[..8];
        var mixed = char.ToUpperInvariant(lower[0]) + lower[1..];

        // A type that does not exist yet, so the importer takes the create branch rather than
        // matching an existing row. That branch is the one the index has to police.
        var imported = await _client.PostAsJsonAsync(
            "/api/portability/import", Bundle(mixed, deliverable: true, title: "Case Variant"));
        imported.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            imported.StatusCode, await imported.Content.ReadAsStringAsync());

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IQuerySession>();
        var stored = await session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .Where(x => x.Name == lower || x.Name == mixed)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Select(x => x.Name).Should().BeEquivalentTo([lower],
            "a create would have stored this name lowercased, and the unique index compares the "
            + "stored value, so an importer that keeps the file's spelling leaves a name the index "
            + "will not recognise as a duplicate of the created one");
    }
}
