using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// Adding a field to a content type that already exists.
/// </summary>
/// <remarks>
/// The claim this endpoint has to make good on is that content types are defined at runtime. Before
/// it, a type could be created and read but never extended: the only route to a new field was the
/// SEO endpoint, which does this same mutation for one hardcoded set. A client asking for one more
/// field on a type already holding their content had no answer short of recreating the type.
///
/// Two of these tests are about refusing rather than adding, and they are the reason the endpoint is
/// more than three lines. A duplicate name must not silently overwrite a field somebody else
/// configured, and a required field with no default must not be allowed to invalidate every entry
/// that already exists.
/// </remarks>
[Collection("Sequential")]
public class AddFieldTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public AddFieldTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<string> TypeAsync(params FieldDefinition[] extra)
    {
        var name = "addfield-" + Guid.NewGuid().ToString("n")[..12];
        var fields = new List<FieldDefinition>
        {
            new() { Name = "Title", DisplayName = "Title", Type = "string" },
        };
        fields.AddRange(extra);

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = fields,
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task EntryAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Title"] = "an entry" },
        });
        await session.SaveChangesAsync();
    }

    private async Task<ContentTypeDefinition> ReadAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type))!;
    }

    [Fact]
    public async Task A_field_added_to_an_existing_type_is_stored_on_it()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Subtitle",
            displayName = "Subtitle",
            type = "string",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var definition = await ReadAsync(type);
        definition.Fields.Should().HaveCount(2, "the type started with one field and gained one");
        definition.Fields.Select(f => f.Name).Should().Contain("Subtitle");
    }

    /// <summary>
    /// The field a block or widget list needs. A json field is the reason this endpoint exists at
    /// all, so it is asserted rather than assumed to fall out of the generic path.
    /// </summary>
    [Fact]
    public async Task A_json_field_can_be_added_so_a_page_can_hold_blocks()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Blocks",
            type = "json",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = (await ReadAsync(type)).Fields.Single(f => f.Name == "Blocks");
        stored.Type.Should().Be("json");
        // Left off the request on purpose: the display name falls back to the field name rather
        // than being stored empty, which is what the admin form would render as a blank label.
        stored.DisplayName.Should().Be("Blocks");
    }

    [Fact]
    public async Task A_reference_field_keeps_the_type_it_points_at()
    {
        var target = await TypeAsync();
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Parent",
            type = "reference",
            referenceType = target,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(type)).Fields.Single(f => f.Name == "Parent").ReferenceType.Should().Be(target);
    }

    [Fact]
    public async Task An_unknown_content_type_is_a_404()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types/not-a-real-type/fields", new
        {
            fieldName = "Subtitle",
            type = "string",
        });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Case-insensitively, because every other name comparison in the codebase is, and a "title"
    /// added beside "Title" is two fields that the admin form renders twice and delivery resolves
    /// by whichever came first.
    /// </summary>
    [Fact]
    public async Task A_name_the_type_already_has_is_refused_rather_than_overwriting_it()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "title",
            type = "markdown",
        });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var definition = await ReadAsync(type);
        definition.Fields.Should().HaveCount(1, "the refusal must not have added a second Title");
        definition.Fields.Single().Type.Should().Be("string", "the existing field must be untouched");
    }

    /// <summary>
    /// The invariant worth having. Marking a new field required on a type that already holds entries
    /// declares a rule every one of those entries breaks.
    /// </summary>
    [Fact]
    public async Task A_required_field_with_no_default_is_refused_when_entries_already_exist()
    {
        var type = await TypeAsync();
        await EntryAsync(type);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Author",
            type = "string",
            isRequired = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(type)).Fields.Should().HaveCount(1, "nothing should have been added");
    }

    /// <summary>
    /// The positive control for the rule above. A guard that refused every required field would pass
    /// that test while making the endpoint useless.
    /// </summary>
    [Fact]
    public async Task A_required_field_with_a_default_is_allowed_even_when_entries_exist()
    {
        var type = await TypeAsync();
        await EntryAsync(type);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Status",
            type = "string",
            isRequired = true,
            defaultValue = "draft",
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(type)).Fields.Should().HaveCount(2);
    }

    /// <summary>
    /// The other half: on a type with no entries there is nothing to invalidate, so the rule must
    /// not fire at all.
    /// </summary>
    [Fact]
    public async Task A_required_field_with_no_default_is_allowed_when_the_type_is_empty()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Author",
            type = "string",
            isRequired = true,
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_field_type_the_registry_does_not_know_is_refused()
    {
        var type = await TypeAsync();

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Whatever",
            type = "quantum",
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(type)).Fields.Should().HaveCount(1);
    }

    /// <summary>
    /// Authorisation is behaviour. Changing a content type's shape is manage_content_types, and an
    /// Editor has no business doing it however useful the endpoint is.
    /// </summary>
    [Fact]
    public async Task An_editor_may_not_change_the_shape_of_a_content_type()
    {
        var type = await TypeAsync();

        var editor = _fixture.CreateClient();
        editor.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Editor"));

        var res = await editor.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Subtitle",
            type = "string",
        });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
        (await ReadAsync(type)).Fields.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_anonymous_caller_may_not_change_the_shape_of_a_content_type()
    {
        var type = await TypeAsync();

        var anon = _fixture.CreateClient();
        var res = await anon.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Subtitle",
            type = "string",
        });

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        (await ReadAsync(type)).Fields.Should().HaveCount(1);
    }
}
