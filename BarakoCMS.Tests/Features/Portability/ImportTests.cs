using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// Applying a document that carries both the content types and the entries is the case the
/// <c>contents</c> section exists for: seeding a new site from an export. It is also the case that
/// was broken, because everything an entry needs in order to be found is decided by the type it
/// arrives alongside.
/// </summary>
/// <remarks>
/// An apply into an already-configured site works, which is why this survives casual testing.
/// See issue #168.
/// </remarks>
[Collection("Integration1")]
public class ImportTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;
    private HttpClient _client = null!;
    private readonly HttpClient _anonymous;

    public ImportTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
        _anonymous = fixture.CreateClient();
    }

    /// <summary>
    /// The caller needs a stored user, which is a write, which cannot happen in a constructor.
    /// </summary>
    /// <remarks>
    /// The capability gate answers from the stored user's roles rather than from the claim, so a
    /// token minted with no user behind it reaches nothing once the role-name fallback is off.
    /// </remarks>
    public async ValueTask InitializeAsync() => _client = await _callers.SuperAdminAsync(null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Applies a document of <c>contentTypes</c> and <c>contents</c> to the default tenant.</summary>
    private Task<(HttpStatusCode Status, string Body)> ApplyAsync(object bundle, bool dryRun = false)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(bundle));
        return BundleCalls.ApplyAsync(
            _client,
            body.GetProperty("contentTypes").EnumerateArray().Select(e => (object)e),
            body.GetProperty("contents").EnumerateArray().Select(e => (object)e),
            dryRun);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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

        var (status, body) = await ApplyAsync(Bundle(type, deliverable: true, title: "Findable Kumquat"));

        status.Should().Be(HttpStatusCode.OK, body);

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

        var (status, body) = await ApplyAsync(Bundle(type, deliverable: false, title: "Hidden Kumquat"));
        status.Should().Be(HttpStatusCode.OK, body);

        (await _anonymous.GetAsync($"/api/public/{type}/search?q=kumquat")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A record whose type is nowhere to be found is still applied, as an entry of a type with no
    /// definition is on any write while the required-type rule is off.
    /// </summary>
    [Fact]
    public async Task A_record_with_no_content_type_anywhere_is_still_applied()
    {
        var missing = "nosuchtype" + Guid.NewGuid().ToString("n")[..8];

        var (status, body) = await ApplyAsync(new
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

        status.Should().Be(HttpStatusCode.OK, body);
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<Content>().CountAsync(c => c.ContentType == missing, Ct)).Should().Be(1);
    }

    /// <summary>
    /// And a plan names the type and the entry as creates, which it could not do while the type
    /// lookup was a snapshot the plan never added to, and writes nothing.
    /// </summary>
    [Fact]
    public async Task A_plan_names_a_type_the_document_itself_creates_and_writes_nothing()
    {
        var type = "dryrun" + Guid.NewGuid().ToString("n")[..8];

        var (status, body) = await ApplyAsync(Bundle(type, deliverable: true, title: "Preview Kumquat"), dryRun: true);

        status.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("applied").GetBoolean().Should().BeFalse();
        ConfigCallers.Changes(document.RootElement).Should().Contain($"contentTypes/{type} create");
        ConfigCallers.Changes(document.RootElement).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(1);
        document.RootElement.GetProperty("sections").EnumerateArray()
            .SelectMany(sec => sec.GetProperty("errors").EnumerateArray()).Should().BeEmpty(
                "the document carries the type, so the entry is not refused for lacking one");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == type, Ct))
            .Should().BeFalse("a plan writes nothing");
    }

    /// <summary>
    /// A content type applied from a document is stored under the same normalized name a created one
    /// would get.
    /// </summary>
    /// <remarks>
    /// The create endpoint slugified the name to lowercase and a writer that kept the file's own
    /// spelling left a name the unique index, which is on the raw value, would not recognise as a
    /// duplicate of a created one. Racing two applies is not something this suite can do
    /// deterministically, so this asserts the property that closes it: what gets written is
    /// normalized, so the index compares like with like.
    /// </remarks>
    [Fact]
    public async Task An_applied_type_is_stored_under_its_normalized_name()
    {
        var lower = "casing" + Guid.NewGuid().ToString("n")[..8];
        var mixed = char.ToUpperInvariant(lower[0]) + lower[1..];

        // A type that does not exist yet, so the apply takes the create branch rather than
        // matching an existing row. That branch is the one the index has to police.
        var (status, body) = await ApplyAsync(Bundle(mixed, deliverable: true, title: "Case Variant"));
        status.Should().Be(HttpStatusCode.OK, body);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IQuerySession>();
        var stored = await session.Query<barakoCMS.Models.ContentTypeDefinition>()
            .Where(x => x.Name == lower || x.Name == mixed)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Select(x => x.Name).Should().BeEquivalentTo([lower],
            "a create would have stored this name lowercased, and the unique index compares the "
            + "stored value, so a writer that keeps the file's spelling leaves a name the index "
            + "will not recognise as a duplicate of the created one");
    }

    private static object[] ManyFields(int count) =>
        Enumerable.Range(1, count)
            .Select(i => (object)new { name = $"Field{i}", displayName = $"Field {i}", type = "string" })
            .ToArray();

    private static object CappedBundle(string type, int fieldCount, bool deliverable = false) => new
    {
        contentTypes = new[]
        {
            new { name = type, displayName = type, isPubliclyDeliverable = deliverable, fields = ManyFields(fieldCount) },
        },
        contents = new[]
        {
            new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Field1"] = "x" } },
        },
    };

    private async Task StoreTypeAsync(string type, int fieldCount)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = Enumerable.Range(1, fieldCount)
                .Select(i => new FieldDefinition { Name = $"Field{i}", DisplayName = $"Field {i}", Type = "string" })
                .ToList(),
        });
        await session.SaveChangesAsync();
    }

    private async Task<ContentTypeDefinition?> StoredTypeAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type);
    }

    /// <summary>
    /// An apply is another way a content type comes into existence, so the field cap (#650) applies
    /// to it the same as to create.
    /// </summary>
    [Fact]
    public async Task A_document_type_with_one_field_more_than_the_cap_is_refused_and_nothing_is_stored()
    {
        var cap = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Default;
        var type = "importcap" + Guid.NewGuid().ToString("n")[..8];

        var (status, body) = await ApplyAsync(CappedBundle(type, cap + 1));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(type).And.Contain($"at most {cap} fields");

        (await StoredTypeAsync(type)).Should().BeNull("a refused apply must not leave the type behind");
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<barakoCMS.Models.Content>().AnyAsync(c => c.ContentType == type))
            .Should().BeFalse("a refused apply must not create the document's entries either");
    }

    [Fact]
    public async Task An_apply_may_not_grow_a_stored_type_past_the_cap()
    {
        var cap = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Default;
        var type = "importgrow" + Guid.NewGuid().ToString("n")[..8];
        await StoreTypeAsync(type, cap);

        var (status, body) = await ApplyAsync(CappedBundle(type, cap + 1));

        status.Should().Be(HttpStatusCode.BadRequest, body);
        (await StoredTypeAsync(type))!.Fields.Should().HaveCount(cap, "the refused fields must not have been written");
    }

    /// <summary>
    /// The positive control: a type already stored over the cap round-trips through an apply at its
    /// own size, because only growth is refused. This passes with or without the cap.
    /// </summary>
    [Fact]
    public async Task A_type_stored_over_the_cap_can_be_applied_again_at_its_size()
    {
        var cap = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Default;
        var type = "importover" + Guid.NewGuid().ToString("n")[..8];
        await StoreTypeAsync(type, cap + 50);

        var (status, body) = await ApplyAsync(CappedBundle(type, cap + 50, deliverable: true));

        status.Should().Be(HttpStatusCode.OK, body);
        var stored = await StoredTypeAsync(type);
        stored!.Fields.Should().HaveCount(cap + 50);
        stored.IsPubliclyDeliverable.Should().BeTrue("the rest of the applied definition still applies");
    }
}
