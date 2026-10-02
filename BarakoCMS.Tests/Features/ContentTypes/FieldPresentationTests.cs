using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A field's editor hint, section and role, and a type's route template, over HTTP: declared when
/// the type or the field is made, or later on one that is already stored.
/// </summary>
/// <remarks>
/// None of the four changes what an entry may hold, so no test here writes one. The cases about
/// existing data are a type stored with none of the members, which has to read back and keep
/// taking fields, and a stored field that gets its hint afterwards.
/// </remarks>
[Collection("Sequential")]
public class FieldPresentationTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public FieldPresentationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName() => "hint-" + Guid.NewGuid().ToString("n")[..12];

    private static FieldDefinition Field(
        string name, string type = "string", string? editor = null, string? section = null, string? role = null) => new()
    {
        Name = name, DisplayName = name, Type = type, Editor = editor, Section = section, Role = role,
    };

    private async Task<string> StoreTypeAsync(params FieldDefinition[] fields)
    {
        var name = NewName();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = fields.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<ContentTypeDefinition?> ReadAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    private async Task<FieldDefinition> ReadFieldAsync(string type, string field) =>
        (await ReadAsync(type))!.Fields.Single(f => f.Name == field);

    // The list is paged and ordered by name, and the shared database holds many types.
    private async Task<JsonElement> ListedAsync(string name)
    {
        for (var page = 1; page <= 50; page++)
        {
            var res = await _client.GetAsync($"/api/content-types?page={page}&pageSize=100", Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
            var items = doc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) break;
            foreach (var type in items.EnumerateArray())
                if (type.GetProperty("name").GetString() == name) return type.Clone();
        }

        throw new Xunit.Sdk.XunitException($"The type '{name}' is not in the list.");
    }

    private Task<HttpResponseMessage> PutPresentationAsync(string type, string field, object body) =>
        _client.PutAsJsonAsync($"/api/content-types/{type}/fields/{field}/presentation", body, Ct);

    private Task<HttpResponseMessage> PutRouteTemplateAsync(string type, object body) =>
        _client.PutAsJsonAsync($"/api/content-types/{type}/route-template", body, Ct);

    private async Task<List<AuditEvent>> AuditEntriesAsync(string action, string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        return (await session.Query<AuditEvent>().Where(e => e.Action == action).ToListAsync(Ct))
            .Where(e => e.Metadata != null && e.Metadata["contentType"].ToString() == type)
            .ToList();
    }

    // ---- declared with the type ------------------------------------------------------------

    [Fact]
    public async Task Creating_a_type_stores_the_editor_section_role_and_route_template_and_the_schema_returns_them()
    {
        var name = NewName();

        var created = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "School event",
            routeTemplate = "/whats-on/{slug}",
            fields = new object[]
            {
                new { name = "EventName", displayName = "Event name", type = "string", role = "title", section = "Details" },
                new { name = "Sections", displayName = "Sections", type = "json", editor = "blocks", section = "Page" },
                new { name = "Cover", displayName = "Cover", type = "url", editor = "image", section = "Details" },
                new { name = "Slug", displayName = "Slug", type = "slug" },
            },
        }, Ct);
        created.IsSuccessStatusCode.Should().BeTrue(await created.Content.ReadAsStringAsync(Ct));

        var stored = await ReadAsync(name);
        stored.Should().NotBeNull();
        stored!.RouteTemplate.Should().Be("/whats-on/{slug}");
        stored.Fields.Should().HaveCount(4);
        stored.Fields.Single(f => f.Name == "Sections").Editor.Should().Be("blocks");
        stored.Fields.Single(f => f.Name == "EventName").Role.Should().Be("title");
        stored.Fields.Single(f => f.Name == "Cover").Section.Should().Be("Details");

        var listed = await ListedAsync(name);
        listed.GetProperty("routeTemplate").GetString().Should().Be("/whats-on/{slug}");

        var fields = listed.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        fields.Should().HaveCount(4);

        fields["EventName"].GetProperty("role").GetString().Should().Be("title");
        fields["EventName"].GetProperty("section").GetString().Should().Be("Details");
        fields["EventName"].GetProperty("editor").ValueKind.Should().Be(JsonValueKind.Null);
        fields["Sections"].GetProperty("editor").GetString().Should().Be("blocks");
        fields["Sections"].GetProperty("section").GetString().Should().Be("Page");
        fields["Cover"].GetProperty("editor").GetString().Should().Be("image");
        fields["Slug"].GetProperty("editor").ValueKind.Should().Be(JsonValueKind.Null);
        fields["Slug"].GetProperty("section").ValueKind.Should().Be(JsonValueKind.Null);
        fields["Slug"].GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Creating_a_type_with_an_unknown_editor_is_a_400_naming_the_accepted_values_and_stores_nothing()
    {
        var name = NewName();

        var refused = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = "Unknown hint",
            fields = new[] { new { name = "Sections", displayName = "Sections", type = "json", editor = "kanban-board" } },
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await refused.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Sections").And.Contain("blocks").And.Contain("menu").And.Contain("links").And.Contain("image");
        body.Should().NotContain("kanban-board");
        (await ReadAsync(name)).Should().BeNull();
    }

    [Fact]
    public async Task Creating_a_type_whose_two_fields_declare_one_role_is_a_400_and_stores_nothing()
    {
        var name = NewName();

        var refused = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = "Two titles",
            fields = new[]
            {
                new { name = "EventName", displayName = "Event name", type = "string", role = "title" },
                new { name = "Headline", displayName = "Headline", type = "string", role = "title" },
            },
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should()
            .Contain("title").And.Contain("EventName").And.Contain("Headline");
        (await ReadAsync(name)).Should().BeNull();
    }

    [Theory]
    [InlineData("@other.example/{slug}")]
    [InlineData("https://other.example/{slug}")]
    [InlineData("/no-slug")]
    [InlineData("//other.example/{slug}")]
    [InlineData("/../{slug}")]
    public async Task Creating_a_type_with_a_route_template_that_is_not_a_path_holding_the_slug_is_a_400(string template)
    {
        var name = NewName();

        var refused = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = "Bad route", routeTemplate = template,
            fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("routeTemplate");
        (await ReadAsync(name)).Should().BeNull();
    }

    // ---- declared with the field -----------------------------------------------------------

    [Fact]
    public async Task Adding_a_field_carries_its_editor_section_and_role()
    {
        var type = await StoreTypeAsync(Field("Title"));

        var added = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "FooterItems", type = "json", editor = "links", section = "Footer",
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));

        var summary = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Teaser", type = "text", role = "summary",
        }, Ct);
        summary.StatusCode.Should().Be(HttpStatusCode.OK, await summary.Content.ReadAsStringAsync(Ct));

        var footer = await ReadFieldAsync(type, "FooterItems");
        footer.Editor.Should().Be("links");
        footer.Section.Should().Be("Footer");
        footer.Role.Should().BeNull();
        (await ReadFieldAsync(type, "Teaser")).Role.Should().Be("summary");
    }

    [Fact]
    public async Task Adding_a_field_with_a_role_a_stored_field_holds_or_an_unknown_editor_is_a_400_and_adds_nothing()
    {
        var type = await StoreTypeAsync(Field("EventName", role: "title"));

        var second = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Headline", type = "string", role = "title",
        }, Ct);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync(Ct)).Should().Contain("title").And.Contain("EventName");

        var unknown = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Sections", type = "json", editor = "Blocks",
        }, Ct);
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync(Ct)).Should().Contain("Accepted values");

        var fields = (await ReadAsync(type))!.Fields;
        fields.Should().HaveCount(1);
        fields[0].Name.Should().Be("EventName");
    }

    // ---- existing data ---------------------------------------------------------------------

    [Fact]
    public async Task A_type_stored_with_none_of_the_members_reads_back_with_nulls_and_still_takes_a_field()
    {
        var type = await StoreTypeAsync(Field("Title"), Field("Body", "markdown"));

        var listed = await ListedAsync(type);
        listed.GetProperty("routeTemplate").ValueKind.Should().Be(JsonValueKind.Null);
        var fields = listed.GetProperty("fields").EnumerateArray().ToList();
        fields.Should().HaveCount(2);
        foreach (var field in fields)
        {
            field.GetProperty("editor").ValueKind.Should().Be(JsonValueKind.Null);
            field.GetProperty("section").ValueKind.Should().Be(JsonValueKind.Null);
            field.GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);
        }

        var added = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Subtitle", type = "string",
        }, Ct);
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
        (await ReadAsync(type))!.Fields.Should().HaveCount(3);
    }

    // ---- a stored field gets its hint afterwards -------------------------------------------

    [Fact]
    public async Task The_presentation_of_a_stored_field_can_be_set_and_cleared_and_the_change_is_audited_once()
    {
        var type = await StoreTypeAsync(Field("Title"), Field("Sections", "json"));

        var set = await PutPresentationAsync(type, "Sections", new { editor = "blocks", section = "Page" });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));

        var answer = await set.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("name").GetString().Should().Be(type);
        answer.GetProperty("field").GetString().Should().Be("Sections");
        answer.GetProperty("editor").GetString().Should().Be("blocks");
        answer.GetProperty("section").GetString().Should().Be("Page");
        answer.GetProperty("role").ValueKind.Should().Be(JsonValueKind.Null);

        var stored = await ReadFieldAsync(type, "Sections");
        stored.Editor.Should().Be("blocks");
        stored.Section.Should().Be("Page");
        stored.Type.Should().Be("json");
        (await ReadAsync(type))!.Fields.Should().HaveCount(2);

        const string action = "contenttype.field.presentation.changed";
        (await AuditEntriesAsync(action, type)).Should().HaveCount(1);

        var repeated = await PutPresentationAsync(type, "Sections", new { editor = "blocks", section = "Page" });
        repeated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditEntriesAsync(action, type)).Should().HaveCount(1, "a request that changes nothing records nothing");

        var cleared = await PutPresentationAsync(type, "sections", new { });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(Ct));

        stored = await ReadFieldAsync(type, "Sections");
        stored.Editor.Should().BeNull();
        stored.Section.Should().BeNull();
        (await AuditEntriesAsync(action, type)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_presentation_the_type_would_refuse_is_a_400_and_the_stored_field_is_unchanged()
    {
        var type = await StoreTypeAsync(
            Field("EventName", role: "title"), Field("Headline"), Field("Sections", "json", editor: "blocks"));

        var unknown = await PutPresentationAsync(type, "Sections", new { editor = "kanban-board" });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await unknown.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("Accepted values").And.Contain("blocks");
        body.Should().NotContain("kanban-board");

        var wrongType = await PutPresentationAsync(type, "Headline", new { editor = "blocks" });
        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongType.Content.ReadAsStringAsync(Ct)).Should().Contain("Headline").And.Contain("json");

        var taken = await PutPresentationAsync(type, "Headline", new { role = "title" });
        taken.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await taken.Content.ReadAsStringAsync(Ct)).Should().Contain("title").And.Contain("EventName");

        foreach (var section in new[] { "", " Details", new string('a', 61), "Two\nlines" })
        {
            var badSection = await PutPresentationAsync(type, "Headline", new { section });
            badSection.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a section of '{0}' is refused", section);
            (await badSection.Content.ReadAsStringAsync(Ct)).Should().Contain("Headline").And.Contain("section");
        }

        (await ReadFieldAsync(type, "Sections")).Editor.Should().Be("blocks");
        var headline = await ReadFieldAsync(type, "Headline");
        headline.Editor.Should().BeNull();
        headline.Role.Should().BeNull();
        headline.Section.Should().BeNull();
        (await ReadFieldAsync(type, "EventName")).Role.Should().Be("title");
    }

    [Fact]
    public async Task A_field_keeps_its_own_role_when_its_presentation_is_sent_again()
    {
        var type = await StoreTypeAsync(Field("EventName", role: "title"));

        var again = await PutPresentationAsync(type, "EventName", new { role = "title", section = "Details" });

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync(Ct));
        var stored = await ReadFieldAsync(type, "EventName");
        stored.Role.Should().Be("title");
        stored.Section.Should().Be("Details");
    }

    [Fact]
    public async Task The_presentation_of_a_field_or_a_type_that_is_not_there_is_a_404()
    {
        var type = await StoreTypeAsync(Field("Title"));

        (await PutPresentationAsync(type, "NoSuchField", new { editor = "image" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutPresentationAsync(NewName(), "Title", new { editor = "image" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Both endpoints read through the caller's tenant, so a type of the same name in another
    /// tenant is not found and is not written.
    /// </summary>
    [Fact]
    public async Task Neither_endpoint_finds_or_changes_a_type_that_lives_in_another_tenant()
    {
        var other = $"hnt-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var type = NewName();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = other, Name = other, IsActive = true });
            await session.SaveChangesAsync(Ct);
        }

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession(other))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [Field("Title"), Field("Slug", "slug")],
            });
            await session.SaveChangesAsync(Ct);
        }

        (await PutPresentationAsync(type, "Title", new { section = "Details" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PutRouteTemplateAsync(type, new { routeTemplate = "/news/{slug}" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using var query = store.QuerySession(other);
        var stored = await query.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        stored.RouteTemplate.Should().BeNull();
        stored.Fields.Should().HaveCount(2);
        stored.Fields.Should().OnlyContain(f => f.Section == null);
    }

    // ---- a stored type gets its route template afterwards ----------------------------------

    [Fact]
    public async Task The_route_template_of_a_stored_type_can_be_set_and_cleared_and_the_change_is_audited_once()
    {
        var type = await StoreTypeAsync(Field("Title"), Field("Slug", "slug"));

        var set = await PutRouteTemplateAsync(type, new { routeTemplate = "/news/{slug}" });
        set.StatusCode.Should().Be(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));

        var answer = await set.Content.ReadFromJsonAsync<JsonElement>(Ct);
        answer.GetProperty("name").GetString().Should().Be(type);
        answer.GetProperty("routeTemplate").GetString().Should().Be("/news/{slug}");

        var stored = await ReadAsync(type);
        stored!.RouteTemplate.Should().Be("/news/{slug}");
        stored.Fields.Should().HaveCount(2);

        const string action = "contenttype.routetemplate.changed";
        (await AuditEntriesAsync(action, type)).Should().HaveCount(1);

        (await PutRouteTemplateAsync(type, new { routeTemplate = "/news/{slug}" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditEntriesAsync(action, type)).Should().HaveCount(1, "a request that changes nothing records nothing");

        var cleared = await PutRouteTemplateAsync(type, new { routeTemplate = (string?)null });
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, await cleared.Content.ReadAsStringAsync(Ct));
        (await ReadAsync(type))!.RouteTemplate.Should().BeNull();
    }

    [Fact]
    public async Task A_route_template_that_is_not_a_path_is_a_400_and_a_missing_type_is_a_404()
    {
        var type = await StoreTypeAsync(Field("Title"), Field("Slug", "slug"));

        foreach (var template in new[] { "@other.example/{slug}", "//other.example/{slug}", "/../{slug}" })
        {
            var refused = await PutRouteTemplateAsync(type, new { routeTemplate = template });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is not a path on the site", template);
            (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("routeTemplate");
        }

        (await ReadAsync(type))!.RouteTemplate.Should().BeNull();

        (await PutRouteTemplateAsync(NewName(), new { routeTemplate = "/news/{slug}" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
