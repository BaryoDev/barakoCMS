using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Events;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// PUT /api/content-types/{name}/fields/{field}/sensitivity.
/// </summary>
/// <remarks>
/// The two directions are different operations wearing one name, so they are tested as two.
///
/// Raising has to stop the value being MATCHED, not only stop it being RETURNED. Anonymous search
/// runs over <c>Content.SearchText</c>, a column derived from whichever fields were Public the last
/// time each entry was written, so an endpoint that updates the definition and stops there leaves
/// every existing value searchable by a caller who may no longer read it. Asserting the field is
/// absent from a response body passes against exactly that bug, which is why the search assertions
/// below are the ones that matter.
///
/// Lowering is a disclosure of data written under the old promise, so it is refused unless the
/// request says it means it, and it is recorded under an audit action of its own.
/// </remarks>
[Collection("Sequential")]
public class FieldSensitivityChangeTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _anon;

    public FieldSensitivityChangeTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _anon = factory.CreateClient();
    }

    private HttpClient Client(params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            // Minted through the fixture rather than by logging in over HTTP: /api/auth/* is rate
            // limited to five requests per fifteen minutes per IP, shared across the whole suite.
            _factory.CreateToken(roles, Guid.NewGuid().ToString()));
        return client;
    }

    private sealed record Seed(string Type, string Marker, string Slug, Guid ContentId);

    /// <summary>
    /// A deliverable type with a Public "Secret" field, and one Published entry whose Secret holds a
    /// distinctive marker, indexed the way the write paths index it.
    /// </summary>
    private async Task<Seed> SeedAsync(
        SensitivityLevel secretLevel = SensitivityLevel.Public,
        bool deliverable = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var type = $"fs{tag}";
        var marker = $"zephyrium{tag}";
        var slug = $"s-{tag}";
        var id = Guid.NewGuid();

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Field sensitivity probe",
            IsPubliclyDeliverable = deliverable,
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Title", Type = "string", Sensitivity = SensitivityLevel.Public },
                new() { Name = "Slug", Type = "slug", Sensitivity = SensitivityLevel.Public },
                new() { Name = "Secret", Type = "string", Sensitivity = secretLevel },
            },
        });

        var data = new Dictionary<string, object>
        {
            ["Title"] = "A title with nothing distinctive in it",
            ["Slug"] = slug,
            ["Secret"] = marker,
        };

        // SearchText carries the marker only while Secret is Public, which is what the create and
        // update endpoints do. Seeding it any other way would test the seeding.
        var indexed = secretLevel == SensitivityLevel.Public
            ? string.Join(' ', data.Values.Select(v => v.ToString()))
            : string.Join(' ', new[] { data["Title"], data["Slug"] }.Select(v => v.ToString()));

        session.Events.StartStream<Content>(
            id,
            new ContentCreated(id, type, data, ContentStatus.Published, Guid.NewGuid(), indexed, SensitivityLevel.Public));

        session.Store(new Content
        {
            Id = id,
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = data,
            SearchText = indexed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await session.SaveChangesAsync();

        return new Seed(type, marker, slug, id);
    }

    private static Task<HttpResponseMessage> SetSensitivityAsync(
        HttpClient client, string type, string field, object body) =>
        client.PutAsJsonAsync($"/api/content-types/{type}/fields/{field}/sensitivity", body);

    /// <summary>
    /// Anonymous search for the marker: how many entries matched, and the results array on its own.
    /// </summary>
    /// <remarks>
    /// The results array rather than the whole body, because the response echoes the query back, so
    /// asserting the raw body does not contain the marker can never pass and asserting it does
    /// contain it passes against a search that matched nothing.
    /// </remarks>
    private async Task<(int Count, string Results)> SearchAsync(string type, string marker)
    {
        var body = await (await _anon.GetAsync($"/api/public/{type}/search?q={marker}"))
            .Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (document.RootElement.GetProperty("count").GetInt32(),
                document.RootElement.GetProperty("results").GetRawText());
    }

    [Fact]
    public async Task Raising_a_field_stops_it_being_delivered_and_stops_it_being_matched()
    {
        var seed = await SeedAsync();

        // The control. Without it every assertion below is satisfied by a type that never worked.
        (await (await _anon.GetAsync($"/api/public/{seed.Type}")).Content.ReadAsStringAsync())
            .Should().Contain(seed.Marker, "the field is Public to begin with");
        var before = await SearchAsync(seed.Type, seed.Marker);
        before.Count.Should().Be(1, "and anonymous search matches it to begin with");
        before.Results.Should().Contain(seed.Marker);

        var res = await SetSensitivityAsync(Client("Admin"), seed.Type, "Secret", new { sensitivity = "Sensitive" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var listed = await (await _anon.GetAsync($"/api/public/{seed.Type}")).Content.ReadAsStringAsync();
        listed.Should().NotContain(seed.Marker, "a Sensitive field is not projected into public delivery");

        var bySlug = await (await _anon.GetAsync($"/api/public/{seed.Type}/{seed.Slug}")).Content.ReadAsStringAsync();
        bySlug.Should().NotContain(seed.Marker, "the slug route reads the same projection");

        // The assertion the issue is about. The response body no longer carries the value either
        // way; what changes here is whether the entry can still be FOUND by searching for it.
        var after = await SearchAsync(seed.Type, seed.Marker);
        after.Count.Should().Be(
            0,
            "an entry that still matches a value nobody may read hands it over one guess at a time");
        after.Results.Should().NotContain(seed.Marker);
    }

    [Fact]
    public async Task Raising_a_field_masks_it_on_the_authenticated_read_path_too()
    {
        var seed = await SeedAsync(deliverable: false);
        var reader = await ReaderAsync(seed.Type);

        (await (await reader.GetAsync($"/api/contents/{seed.ContentId}")).Content.ReadAsStringAsync())
            .Should().Contain(seed.Marker, "a Public field is readable by anyone who can read the type");

        var res = await SetSensitivityAsync(Client("Admin"), seed.Type, "Secret", new { sensitivity = "Sensitive" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        (await (await reader.GetAsync($"/api/contents/{seed.ContentId}")).Content.ReadAsStringAsync())
            .Should().NotContain(seed.Marker, "the authoring API masks it from a reader outside the allowed roles");
    }

    /// <summary>A user who may read the type, in a token role that is not HR or SuperAdmin.</summary>
    private async Task<HttpClient> ReaderAsync(string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new() { ContentTypeSlug = contentType, Read = new PermissionRule { Enabled = true } },
            },
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"reader_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            RoleIds = new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(new[] { "Editor" }, user.Id.ToString()));
        return client;
    }

    [Fact]
    public async Task Lowering_is_refused_unless_the_request_acknowledges_the_disclosure()
    {
        var seed = await SeedAsync(SensitivityLevel.Sensitive);

        var refused = await SetSensitivityAsync(
            Client("Admin"), seed.Type, "Secret", new { sensitivity = "Public" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var message = await refused.Content.ReadAsStringAsync();
        message.Should().Contain("acknowledgeDisclosure", "the refusal has to say how to proceed");
        message.Should().Contain("1 existing entry", "and how much data the decision covers");
        message.Should().Contain("anonymous", "and that this type is served anonymously");

        (await SearchAsync(seed.Type, seed.Marker)).Count
            .Should().Be(0, "a refused request changes nothing");
        (await (await _anon.GetAsync($"/api/public/{seed.Type}")).Content.ReadAsStringAsync())
            .Should().NotContain(seed.Marker);
    }

    [Fact]
    public async Task Lowering_with_the_acknowledgement_serves_the_value_and_reindexes_the_entries()
    {
        var seed = await SeedAsync(SensitivityLevel.Sensitive);

        (await SearchAsync(seed.Type, seed.Marker)).Count
            .Should().Be(0, "the control: it is not searchable while Sensitive");

        var res = await SetSensitivityAsync(
            Client("Admin"), seed.Type, "Secret",
            new { sensitivity = "Public", acknowledgeDisclosure = true });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("from").GetString().Should().Be("Sensitive");
        body.GetProperty("to").GetString().Should().Be("Public");
        body.GetProperty("entriesReindexed").GetInt32().Should().Be(1);

        (await (await _anon.GetAsync($"/api/public/{seed.Type}")).Content.ReadAsStringAsync())
            .Should().Contain(seed.Marker, "the field is Public now");

        // Without the reindex the value is returned but not findable, which is the mirror of the
        // raising bug and just as invisible from a response body.
        var found = await SearchAsync(seed.Type, seed.Marker);
        found.Count.Should().Be(1, "and anonymous search finds it");
        found.Results.Should().Contain(seed.Marker);
    }

    [Fact]
    public async Task The_rebuilt_search_text_survives_a_rebuild_from_the_stream()
    {
        var seed = await SeedAsync();

        var res = await SetSensitivityAsync(Client("Admin"), seed.Type, "Secret", new { sensitivity = "Hidden" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stream = await session.Events.FetchStreamAsync(seed.ContentId);
        stream.Should().HaveCount(2, "creation, then the scrub");

        // Replayed rather than loaded. A scrub written straight to the document holds until the next
        // projection rebuild, which replays the creation event and puts the marker back, and nothing
        // about that rebuild would look wrong.
        var rebuilt = new Content();
        foreach (var e in stream)
        {
            switch (e.Data)
            {
                case ContentCreated x: rebuilt.Apply(x, e.Timestamp.UtcDateTime); break;
                case ContentFieldSensitivityChanged x: rebuilt.Apply(x, e.Timestamp.UtcDateTime); break;
                default: throw new InvalidOperationException($"unexpected {e.Data.GetType().Name} on the stream");
            }
        }

        rebuilt.SearchText.Should().NotBeNullOrWhiteSpace("the entry is still indexed on its public fields");
        rebuilt.SearchText.Should().NotContain(seed.Marker, "and the scrub is on the stream, not only on the document");
        rebuilt.Data.Should().ContainKey("Secret",
            "raising sensitivity stops the value being served, it does not delete it");
    }

    [Fact]
    public async Task The_scrub_does_not_restamp_the_entry_as_edited()
    {
        var seed = await SeedAsync();

        DateTime before;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            before = (await session.LoadAsync<Content>(seed.ContentId))!.UpdatedAt;
        }

        var res = await SetSensitivityAsync(Client("Admin"), seed.Type, "Secret", new { sensitivity = "Sensitive" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var after = (await session.LoadAsync<Content>(seed.ContentId))!;

            after.SearchText.Should().NotContain(seed.Marker, "the control: the scrub did run");
            after.UpdatedAt.Should().Be(
                before,
                "nobody edited this entry, and restamping it would move every entry of the type to the "
              + "top of every recently-updated list and change what the sitemap reports as lastmod");
        }
    }

    [Fact]
    public async Task Both_directions_are_audited_and_lowering_carries_its_own_action()
    {
        var seed = await SeedAsync();

        (await SetSensitivityAsync(Client("Admin"), seed.Type, "Secret", new { sensitivity = "Sensitive" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetSensitivityAsync(
                Client("Admin"), seed.Type, "Secret",
                new { sensitivity = "Public", acknowledgeDisclosure = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var entries = (await session.Query<AuditEvent>()
                .Where(e => e.Action.StartsWith("contenttype.field.sensitivity"))
                .ToListAsync())
            .Where(e => e.Metadata != null && e.Metadata["contentType"].ToString() == seed.Type)
            .ToList();

        entries.Should().HaveCount(2, "both directions are audited, or the trail says nothing happened");

        var raised = entries.Single(e => e.Action == "contenttype.field.sensitivity.changed");
        raised.Metadata!["field"].ToString().Should().Be("Secret");
        raised.Metadata["from"].ToString().Should().Be("Public");
        raised.Metadata["to"].ToString().Should().Be("Sensitive");
        raised.TargetType.Should().Be("ContentType");

        // A disclosure needs an action you can alert on without reading the metadata of every
        // sensitivity change ever made.
        var lowered = entries.Single(e => e.Action == "contenttype.field.sensitivity.lowered");
        lowered.Metadata!["from"].ToString().Should().Be("Sensitive");
        lowered.Metadata["to"].ToString().Should().Be("Public");
        lowered.ActorUserId.Should().NotBeNull("an unattributed disclosure is not an audit trail");
    }

    [Fact]
    public async Task Raising_replaces_the_role_list_and_lowering_clears_it()
    {
        var seed = await SeedAsync();

        var raised = await SetSensitivityAsync(
            Client("Admin"), seed.Type, "Secret",
            new { sensitivity = "Sensitive", visibleToRoles = new[] { "HR" }, mask = "Last4" });
        raised.StatusCode.Should().Be(HttpStatusCode.OK, await raised.Content.ReadAsStringAsync());

        var lowered = await SetSensitivityAsync(
            Client("Admin"), seed.Type, "Secret",
            new { sensitivity = "Public", acknowledgeDisclosure = true });
        lowered.StatusCode.Should().Be(HttpStatusCode.OK, await lowered.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == seed.Type);
        var field = def!.Fields.Single(f => f.Name == "Secret");

        field.Sensitivity.Should().Be(SensitivityLevel.Public);
        field.VisibleToRoles.Should().BeEmpty(
            "a Public field listing the roles that may see it reads as a restriction that is not there, "
          + "and raising the level again would silently reinstate the stale list");
        field.Mask.Should().Be(FieldMask.Default, "there is nothing left to mask");
    }

    [Fact]
    public async Task A_public_target_refuses_a_role_list_rather_than_dropping_it()
    {
        var seed = await SeedAsync(SensitivityLevel.Sensitive);

        var res = await SetSensitivityAsync(
            Client("Admin"), seed.Type, "Secret",
            new { sensitivity = "Public", acknowledgeDisclosure = true, visibleToRoles = new[] { "HR" } });

        res.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "dropping it silently leaves the caller believing they restricted the field to HR");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == seed.Type);
        def!.Fields.Single(f => f.Name == "Secret").Sensitivity.Should().Be(
            SensitivityLevel.Sensitive, "a refused request changes nothing");
    }

    [Fact]
    public async Task Only_an_admin_can_change_a_field_s_sensitivity()
    {
        var seed = await SeedAsync();

        (await SetSensitivityAsync(_anon, seed.Type, "Secret", new { sensitivity = "Sensitive" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await SetSensitivityAsync(Client("Editor"), seed.Type, "Secret", new { sensitivity = "Sensitive" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await SetSensitivityAsync(
                Client("Editor"), seed.Type, "Secret",
                new { sensitivity = "Public", acknowledgeDisclosure = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "the lowering direction is gated the same way");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == seed.Type);
        def!.Fields.Single(f => f.Name == "Secret").Sensitivity.Should().Be(
            SensitivityLevel.Public, "a refused caller changed nothing");
    }

    [Fact]
    public async Task An_unknown_type_or_field_is_a_404()
    {
        var seed = await SeedAsync();
        var admin = Client("Admin");

        (await SetSensitivityAsync(admin, $"nosuch{Guid.NewGuid():N}", "Secret", new { sensitivity = "Sensitive" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await SetSensitivityAsync(admin, seed.Type, "NoSuchField", new { sensitivity = "Sensitive" }))
            .StatusCode.Should().Be(
                HttpStatusCode.NotFound,
                "succeeding silently on a field nobody has would report a masking that never happened");
    }

    [Fact]
    public async Task A_field_is_addressed_case_insensitively_like_everywhere_else()
    {
        var seed = await SeedAsync();

        var res = await SetSensitivityAsync(Client("Admin"), seed.Type, "secret", new { sensitivity = "Sensitive" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        (await (await _anon.GetAsync($"/api/public/{seed.Type}")).Content.ReadAsStringAsync())
            .Should().NotContain(seed.Marker);
    }
}
