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
/// A choice field: a value from a list the field declares, or a list of them.
/// </summary>
/// <remarks>
/// Before it, an entry type, a shirt size or an area of focus was a free string, so a typo was valid
/// data and nothing stable existed to filter on or to key a colour to. Every refusal here is a value
/// that used to be accepted.
/// </remarks>
[Collection("Sequential")]
public class ChoiceFieldTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ChoiceFieldTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NewName() => "choice-" + Guid.NewGuid().ToString("n")[..12];

    private static FieldDefinition EntryType() => new()
    {
        Name = "EntryType", DisplayName = "Entry type", Type = "choice",
        Options = [new() { Value = "FUN", Label = "Fun run" }, new() { Value = "COMPETE", Label = "Competitive" }],
    };

    private static FieldDefinition Sizes(bool required = false) => new()
    {
        Name = "Sizes", DisplayName = "Sizes", Type = "choice", Multiple = true, IsRequired = required,
        Options = [new() { Value = "S", Label = "Small" }, new() { Value = "M", Label = "Medium" }, new() { Value = "L", Label = "Large" }],
    };

    private async Task<string> StoreTypeAsync(bool deliverable, params FieldDefinition[] fields)
    {
        var name = NewName();
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = name, DisplayName = name, IsPubliclyDeliverable = deliverable,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }, .. fields],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task StoreEntryAsync(string type, string title, Dictionary<string, object> data)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        data["Title"] = title;
        session.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public, Data = data,
        });
        await session.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> CreateEntryAsync(string type, Dictionary<string, object> data) =>
        _client.PostAsJsonAsync("/api/contents", new { contentType = type, data });

    // ---- definition time -------------------------------------------------------------------

    [Fact]
    public async Task A_choice_field_must_list_its_options()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "No options",
            fields = new[] { new { name = "EntryType", type = "choice" } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("must list its options");
    }

    [Fact]
    public async Task Options_that_differ_only_in_case_are_refused()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "Case",
            fields = new[]
            {
                new { name = "EntryType", type = "choice", options = new[] { new { value = "FUN", label = "Fun" }, new { value = "fun", label = "fun" } } },
            },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("more than once");
    }

    [Fact]
    public async Task Options_on_a_field_that_is_not_a_choice_are_refused()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName(), displayName = "Wrong type",
            fields = new[] { new { name = "Title", type = "string", options = new[] { new { value = "A", label = "A" } } } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("not choice");
    }

    [Fact]
    public async Task The_schema_returns_the_options_in_order()
    {
        var type = await StoreTypeAsync(false, EntryType());

        var res = await _client.GetAsync("/api/content-types");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
        var definition = list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == type);
        var field = definition.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "EntryType");

        var options = field.GetProperty("options").EnumerateArray().ToList();
        options.Should().HaveCount(2);
        options.Select(o => o.GetProperty("value").GetString()).Should().Equal("FUN", "COMPETE");
        options.Select(o => o.GetProperty("label").GetString()).Should().Equal("Fun run", "Competitive");
    }

    [Fact]
    public async Task Adding_a_choice_field_carries_its_options()
    {
        var type = await StoreTypeAsync(false);

        var res = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Gender", type = "choice",
            options = new[] { new { value = "F", label = "Female" }, new { value = "M", label = "Male" } },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var bad = await _client.PostAsJsonAsync($"/api/content-types/{type}/fields", new { fieldName = "Shirt", type = "choice" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- write time ------------------------------------------------------------------------

    [Fact]
    public async Task A_value_that_is_not_an_option_is_refused_naming_the_accepted_values()
    {
        var type = await StoreTypeAsync(false, EntryType());

        var wrong = await CreateEntryAsync(type, new() { ["Title"] = "a", ["EntryType"] = "fun" });
        wrong.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "values are matched exactly, so a lower-case typo is the drift the type exists to stop");
        (await wrong.Content.ReadAsStringAsync()).Should().Contain("FUN, COMPETE").And.Contain("'fun'");

        var right = await CreateEntryAsync(type, new() { ["Title"] = "b", ["EntryType"] = "FUN" });
        right.IsSuccessStatusCode.Should().BeTrue(await right.Content.ReadAsStringAsync());

        var list = await CreateEntryAsync(type, new() { ["Title"] = "c", ["EntryType"] = new[] { "FUN" } });
        list.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a single choice holds one value, not a list");
    }

    [Fact]
    public async Task A_multiple_choice_takes_a_list_of_its_options()
    {
        var type = await StoreTypeAsync(false, Sizes());

        var ok = await CreateEntryAsync(type, new() { ["Title"] = "a", ["Sizes"] = new[] { "S", "L" } });
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync());

        var unknown = await CreateEntryAsync(type, new() { ["Title"] = "b", ["Sizes"] = new[] { "S", "XL" } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("'XL'");

        var single = await CreateEntryAsync(type, new() { ["Title"] = "c", ["Sizes"] = "S" });
        single.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a multiple choice takes a list, even of one");

        var repeated = await CreateEntryAsync(type, new() { ["Title"] = "d", ["Sizes"] = new[] { "S", "S" } });
        repeated.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_required_multiple_choice_with_nothing_chosen_is_refused()
    {
        var type = await StoreTypeAsync(false, Sizes(required: true));

        var empty = await CreateEntryAsync(type, new() { ["Title"] = "a", ["Sizes"] = Array.Empty<string>() });
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await empty.Content.ReadAsStringAsync()).Should().Contain("is required");
    }

    // ---- delivery --------------------------------------------------------------------------

    private sealed record Item(Dictionary<string, JsonElement> Data);
    private sealed record Page(List<Item> Items, int TotalItems);

    private async Task<List<string>> TitlesAsync(string url)
    {
        var res = await _fixture.CreateClient().GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var page = await res.Content.ReadFromJsonAsync<Page>();
        return page!.Items.Select(i => i.Data["Title"].GetString()!).OrderBy(t => t).ToList();
    }

    [Fact]
    public async Task Delivery_filters_a_choice_by_value_and_a_list_by_what_it_holds()
    {
        var type = await StoreTypeAsync(true, EntryType(), Sizes());
        await StoreEntryAsync(type, "one", new() { ["EntryType"] = "FUN", ["Sizes"] = new List<object> { "S", "M" } });
        await StoreEntryAsync(type, "two", new() { ["EntryType"] = "COMPETE", ["Sizes"] = new List<object> { "M" } });
        await StoreEntryAsync(type, "three", new() { ["EntryType"] = "FUN", ["Sizes"] = new List<object> { "L" } });

        (await TitlesAsync($"/api/public/{type}?filter[EntryType][eq]=FUN")).Should().Equal("one", "three");
        (await TitlesAsync($"/api/public/{type}?filter[Sizes][eq]=M")).Should().Equal("one", "two");
        (await TitlesAsync($"/api/public/{type}?filter[Sizes][ne]=M")).Should().Equal("three");

        var ordered = await _fixture.CreateClient().GetAsync($"/api/public/{type}?filter[Sizes][lt]=M");
        ordered.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ordered.Content.ReadAsStringAsync()).Should().Contain("holds a list of options");
    }

    // ---- changing options ------------------------------------------------------------------

    private Task<HttpResponseMessage> PutOptionsAsync(string type, string field, object body) =>
        _client.PutAsJsonAsync($"/api/content-types/{type}/fields/{field}/options", body);

    private async Task<FieldDefinition> ReadFieldAsync(string type, string field)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type);
        return def.Fields.Single(f => f.Name == field);
    }

    [Fact]
    public async Task Rewording_a_label_or_adding_an_option_changes_no_entry()
    {
        var type = await StoreTypeAsync(false, EntryType());
        await StoreEntryAsync(type, "one", new() { ["EntryType"] = "FUN" });

        var res = await PutOptionsAsync(type, "EntryType", new
        {
            options = new[]
            {
                new { value = "FUN", label = "5K fun run" },
                new { value = "COMPETE", label = "Competitive" },
                new { value = "KIDS", label = "Kids dash" },
            },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var field = await ReadFieldAsync(type, "EntryType");
        field.Options!.Select(o => o.Label).Should().Equal("5K fun run", "Competitive", "Kids dash");
    }

    [Fact]
    public async Task Removing_an_option_entries_hold_is_refused_unless_forced()
    {
        var type = await StoreTypeAsync(false, Sizes());
        await StoreEntryAsync(type, "one", new() { ["Sizes"] = new List<object> { "S", "L" } });
        await StoreEntryAsync(type, "two", new() { ["Sizes"] = new List<object> { "L" } });
        await StoreEntryAsync(type, "three", new() { ["Sizes"] = new List<object> { "M" } });

        var withoutL = new[] { new { value = "S", label = "Small" }, new { value = "M", label = "Medium" } };

        var refused = await PutOptionsAsync(type, "Sizes", new { options = withoutL });
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("2 entries hold").And.Contain("'L'");
        (await ReadFieldAsync(type, "Sizes")).Options!.Should().HaveCount(3, "a refused change must not land");

        var forced = await PutOptionsAsync(type, "Sizes", new { options = withoutL, force = true });
        forced.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await forced.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("entriesHoldingRemoved").GetInt32().Should().Be(2);
        (await ReadFieldAsync(type, "Sizes")).Options!.Select(o => o.Value).Should().Equal("S", "M");
    }

    [Fact]
    public async Task Removing_an_option_nobody_holds_needs_no_force()
    {
        var type = await StoreTypeAsync(false, EntryType());
        await StoreEntryAsync(type, "one", new() { ["EntryType"] = "FUN" });

        var res = await PutOptionsAsync(type, "EntryType", new { options = new[] { new { value = "FUN", label = "Fun run" } } });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await ReadFieldAsync(type, "EntryType")).Options!.Should().ContainSingle();
    }

    [Fact]
    public async Task Options_can_only_be_set_on_a_choice_field_that_exists()
    {
        var type = await StoreTypeAsync(false, EntryType());
        var body = new { options = new[] { new { value = "A", label = "A" } } };

        (await PutOptionsAsync(type, "Title", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutOptionsAsync(type, "Missing", body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
