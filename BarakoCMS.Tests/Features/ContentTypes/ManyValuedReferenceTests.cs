using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A reference field with <c>multiple</c> holds a list of ids, each checked on write, resolved by
/// <c>include</c> and matched exactly by the <c>has</c> filter.
/// </summary>
/// <remarks>
/// Before it, an event's speakers or a class's students were an untyped array: nothing checked
/// the targets existed, include left the ids alone, and <c>contains</c> matched any id holding the
/// text asked for.
/// </remarks>
[Collection("Sequential")]
public class ManyValuedReferenceTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public ManyValuedReferenceTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("Admin", "SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NewName(string prefix) => prefix + "-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>A speaker type, and an event type whose Speakers field points at it, many valued.</summary>
    private async Task<(string Speaker, string Event)> StoreTypesAsync(bool required = false)
    {
        var speaker = NewName("mvspeaker");
        var evt = NewName("mvevent");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = speaker, DisplayName = speaker, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Phone", DisplayName = "Phone", Type = "string", Sensitivity = SensitivityLevel.Sensitive,
                },
            ],
        });

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = evt, DisplayName = evt, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition
                {
                    Name = "Speakers", DisplayName = "Speakers", Type = "reference", ReferenceType = speaker,
                    Multiple = true, IsRequired = required,
                },
                new FieldDefinition { Name = "Tags", DisplayName = "Tags", Type = "array" },
            ],
        });

        await session.SaveChangesAsync();
        return (speaker, evt);
    }

    private async Task<Guid> StoreEntryAsync(
        string type,
        Dictionary<string, object> data,
        ContentStatus status = ContentStatus.Published,
        SensitivityLevel sensitivity = SensitivityLevel.Public)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new Content
        {
            Id = id, ContentType = type, Status = status, Sensitivity = sensitivity, Data = data,
        });
        await session.SaveChangesAsync();
        return id;
    }

    private Task<HttpResponseMessage> CreateEventAsync(string type, object speakers) =>
        _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "talks", ["Speakers"] = speakers },
        });

    private static async Task<string> RefusalAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return body;
    }

    // ---- definition time -------------------------------------------------------------------

    [Fact]
    public async Task A_reference_field_may_hold_several_ids()
    {
        var (speaker, _) = await StoreTypesAsync();

        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName("mvdef"), displayName = "Panel",
            fields = new object[]
            {
                new { name = "Title", type = "string" },
                new { name = "Speakers", type = "reference", referenceType = speaker, multiple = true },
            },
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Multiple_is_still_refused_on_a_field_that_is_neither_a_choice_nor_a_reference()
    {
        var res = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = NewName("mvdef"), displayName = "Plain",
            fields = new object[] { new { name = "Title", type = "string", multiple = true } },
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- write time ------------------------------------------------------------------------

    [Fact]
    public async Task A_list_holding_an_id_of_the_wrong_type_is_refused_naming_that_id()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var right = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });
        var wrong = await StoreEntryAsync(evt, new() { ["Title"] = "not a speaker" });

        var body = await RefusalAsync(await CreateEventAsync(evt, new[] { right.ToString(), wrong.ToString() }));

        body.Should().Contain(wrong.ToString(), "the error names the id that points at the wrong type");
        body.Should().NotContain(right.ToString(), "the id of the right type is not the problem");
    }

    [Fact]
    public async Task A_list_holding_an_id_that_does_not_exist_is_refused_naming_that_id()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var right = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });
        var missing = Guid.NewGuid();

        var body = await RefusalAsync(await CreateEventAsync(evt, new[] { right.ToString(), missing.ToString() }));

        body.Should().Contain(missing.ToString());
        body.Should().Contain("not exist");
    }

    [Fact]
    public async Task A_list_of_the_right_type_is_stored_in_the_order_sent()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var first = await StoreEntryAsync(speaker, new() { ["Name"] = "Second by creation" });
        var second = await StoreEntryAsync(speaker, new() { ["Name"] = "First by creation" });

        var res = await CreateEventAsync(evt, new[] { second.ToString(), first.ToString() });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var created = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<Content>(id);

        stored.Should().NotBeNull();
        FieldTypeRegistry.TryReadChoice(stored!.Data["Speakers"], out var ids, out var isList).Should().BeTrue();
        isList.Should().BeTrue();
        ids.Should().HaveCount(2);
        ids.Should().Equal(second.ToString(), first.ToString());
    }

    [Fact]
    public async Task A_single_id_in_a_many_valued_field_is_refused()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var right = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });

        var body = await RefusalAsync(await CreateEventAsync(evt, right.ToString()));

        body.Should().Contain("send a list of ids");
    }

    [Fact]
    public async Task Duplicates_upper_case_ids_and_more_than_the_cap_are_refused()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var right = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });

        (await RefusalAsync(await CreateEventAsync(evt, new[] { right.ToString(), right.ToString() })))
            .Should().Contain("more than once");

        (await RefusalAsync(await CreateEventAsync(evt, new[] { right.ToString().ToUpperInvariant() })))
            .Should().Contain("lower case");

        var tooMany = Enumerable.Range(0, ReferenceFields.MaxIds + 1).Select(_ => Guid.NewGuid().ToString()).ToArray();
        tooMany.Should().HaveCount(101);
        (await RefusalAsync(await CreateEventAsync(evt, tooMany)))
            .Should().Contain($"at most {ReferenceFields.MaxIds} references");
    }

    [Fact]
    public async Task An_empty_list_is_none_and_a_required_field_refuses_it()
    {
        var (_, optional) = await StoreTypesAsync();
        var accepted = await CreateEventAsync(optional, Array.Empty<string>());
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());

        var (_, required) = await StoreTypesAsync(required: true);
        (await RefusalAsync(await CreateEventAsync(required, Array.Empty<string>())))
            .Should().Contain("is required");
    }

    /// <summary>
    /// A bundle import gives every record a new id, so a list naming records of the same bundle has
    /// to be pointed at them as imported, and written after them.
    /// </summary>
    [Fact]
    public async Task A_bundle_import_points_listed_ids_at_the_records_as_imported()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var adaSource = Guid.NewGuid();
        var graceSource = Guid.NewGuid();

        // The panel comes first, so it can only be written once both speakers are.
        var res = await _client.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun = false,
            contentTypes = Array.Empty<object>(),
            contents = new object[]
            {
                new
                {
                    id = Guid.NewGuid(), contentType = evt, status = "Published",
                    data = new Dictionary<string, object>
                    {
                        ["Title"] = "imported panel",
                        ["Speakers"] = new[] { graceSource.ToString(), adaSource.ToString() },
                    },
                },
                new
                {
                    id = adaSource, contentType = speaker, status = "Published",
                    data = new Dictionary<string, object> { ["Name"] = "Ada" },
                },
                new
                {
                    id = graceSource, contentType = speaker, status = "Published",
                    data = new Dictionary<string, object> { ["Name"] = "Grace" },
                },
            },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

        var speakers = await session.Query<Content>().Where(c => c.ContentType == speaker).ToListAsync();
        speakers.Should().HaveCount(2);
        var byName = speakers.ToDictionary(s => s.Data["Name"].ToString()!, s => s.Id.ToString());

        var panels = await session.Query<Content>().Where(c => c.ContentType == evt).ToListAsync();
        panels.Should().HaveCount(1);

        FieldTypeRegistry.TryReadChoice(panels[0].Data["Speakers"], out var ids, out var isList).Should().BeTrue();
        isList.Should().BeTrue();
        ids.Should().HaveCount(2);
        ids.Should().Equal(byName["Grace"], byName["Ada"]);
    }

    /// <summary>
    /// Two entries that list each other, which the API can create (create A, create B listing A,
    /// update A to list B), import back in one bundle. No order writes either one first.
    /// </summary>
    [Fact]
    public async Task A_bundle_import_takes_two_records_that_list_each_other()
    {
        var article = NewName("mvarticle");
        using (var scope = _fixture.Services.CreateScope())
        {
            var setup = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            setup.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = article, DisplayName = article,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition
                    {
                        Name = "RelatedArticles", DisplayName = "Related articles", Type = "reference",
                        ReferenceType = article, Multiple = true,
                    },
                ],
            });
            await setup.SaveChangesAsync();
        }

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var res = await _client.PostAsJsonAsync("/api/portability/import", new
        {
            dryRun = false,
            contentTypes = Array.Empty<object>(),
            contents = new object[]
            {
                new
                {
                    id = first, contentType = article, status = "Published",
                    data = new Dictionary<string, object>
                    {
                        ["Title"] = "first", ["RelatedArticles"] = new[] { second.ToString() },
                    },
                },
                new
                {
                    id = second, contentType = article, status = "Published",
                    data = new Dictionary<string, object>
                    {
                        ["Title"] = "second", ["RelatedArticles"] = new[] { first.ToString() },
                    },
                },
            },
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        using var read = _fixture.Services.CreateScope();
        var session = read.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.Query<Content>().Where(c => c.ContentType == article).ToListAsync();
        stored.Should().HaveCount(2);

        var byTitle = stored.ToDictionary(c => c.Data["Title"].ToString()!);
        foreach (var (title, other) in new[] { ("first", "second"), ("second", "first") })
        {
            FieldTypeRegistry.TryReadChoice(byTitle[title].Data["RelatedArticles"], out var ids, out var isList)
                .Should().BeTrue();
            isList.Should().BeTrue();
            ids.Should().Equal(byTitle[other].Id.ToString());
        }
    }

    // ---- read time -------------------------------------------------------------------------

    [Fact]
    public async Task Include_resolves_every_readable_entry_in_order_and_leaves_out_the_rest()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var ada = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada", ["Phone"] = "555-0101" });
        var draft = await StoreEntryAsync(speaker, new() { ["Name"] = "Drafted Dora" }, ContentStatus.Draft);
        var grace = await StoreEntryAsync(speaker, new() { ["Name"] = "Grace" });
        var hidden = await StoreEntryAsync(
            speaker, new() { ["Name"] = "Hidden Hal" }, sensitivity: SensitivityLevel.Sensitive);

        await StoreEntryAsync(evt, new()
        {
            ["Title"] = "panel",
            ["Speakers"] = new List<object> { grace.ToString(), draft.ToString(), ada.ToString(), hidden.ToString() },
        });

        var res = await _fixture.CreateClient().GetAsync($"/api/public/{evt}?include=Speakers");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        body.Should().NotContain("Drafted Dora", "include is not a way into a draft");
        body.Should().NotContain("Hidden Hal", "nor into a document that is not Public");
        body.Should().NotContain("555-0101", "nor into a field the speaker type keeps Sensitive");
        body.Should().NotContain(draft.ToString(), "an entry left out is not named either");

        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);

        var speakers = items[0].GetProperty("data").GetProperty("Speakers");
        speakers.ValueKind.Should().Be(JsonValueKind.Array);
        speakers.GetArrayLength().Should().Be(2);
        speakers.EnumerateArray()
            .Select(s => s.GetProperty("data").GetProperty("Name").GetString())
            .Should().Equal("Grace", "Ada");
    }

    [Fact]
    public async Task Include_past_the_bound_is_refused_rather_than_resolved_in_part()
    {
        var (_, evt) = await StoreTypesAsync();

        // Eleven entries of a hundred ids each, stored directly: none of the ids needs to exist,
        // since the bound is counted before anything is read.
        for (var i = 0; i < 11; i++)
        {
            await StoreEntryAsync(evt, new()
            {
                ["Title"] = $"crowd {i}",
                ["Speakers"] = Enumerable.Range(0, 100).Select(_ => (object)Guid.NewGuid().ToString()).ToList(),
            });
        }

        var res = await _fixture.CreateClient().GetAsync($"/api/public/{evt}?include=Speakers&pageSize=100");

        (await RefusalAsync(res)).Should().Contain("one request resolves at most 1000");
    }

    [Fact]
    public async Task Has_matches_one_exact_element_where_contains_matches_a_substring()
    {
        var (speaker, evt) = await StoreTypesAsync();
        var ada = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });
        var grace = await StoreEntryAsync(speaker, new() { ["Name"] = "Grace" });

        await StoreEntryAsync(evt, new()
        {
            ["Title"] = "exact",
            ["Speakers"] = new List<object> { ada.ToString() },
            ["Tags"] = new List<object> { "abc-123" },
        });
        await StoreEntryAsync(evt, new()
        {
            ["Title"] = "longer",
            ["Speakers"] = new List<object> { grace.ToString() },
            ["Tags"] = new List<object> { "abc-1234" },
        });

        (await TitlesAsync($"/api/public/{evt}?filter[Tags][contains]=abc-123"))
            .Should().Equal("exact", "longer");

        (await TitlesAsync($"/api/public/{evt}?filter[Tags][has]=abc-123"))
            .Should().Equal("exact");

        (await TitlesAsync($"/api/public/{evt}?filter[Speakers][has]={grace.ToString().ToUpperInvariant()}"))
            .Should().Equal("longer");
    }

    [Fact]
    public async Task Eq_ne_and_has_on_a_list_reference_compare_ids_in_any_case_and_other_operators_are_refused()
    {
        // Both speakers are stored and published: a filter answers against the ids delivery shows,
        // and an id that names no entry is not one of them.
        var (speaker, evt) = await StoreTypesAsync();
        var ada = await StoreEntryAsync(speaker, new() { ["Name"] = "Ada" });
        var grace = await StoreEntryAsync(speaker, new() { ["Name"] = "Grace" });

        // Stored directly, as a bundle that turned an array into a reference would leave it.
        await StoreEntryAsync(evt, new()
        {
            ["Title"] = "upper", ["Speakers"] = new List<object> { ada.ToString().ToUpperInvariant() },
        });
        await StoreEntryAsync(evt, new()
        {
            ["Title"] = "lower", ["Speakers"] = new List<object> { grace.ToString() },
        });

        (await TitlesAsync($"/api/public/{evt}?filter[Speakers][eq]={ada}")).Should().Equal("upper");
        (await TitlesAsync($"/api/public/{evt}?filter[Speakers][has]={ada}")).Should().Equal("upper");
        (await TitlesAsync($"/api/public/{evt}?filter[Speakers][ne]={grace.ToString().ToUpperInvariant()}"))
            .Should().Equal("upper");

        foreach (var op in new[] { "contains", "lt" })
        {
            var res = await _fixture.CreateClient().GetAsync($"/api/public/{evt}?filter[Speakers][{op}]={ada}");
            (await RefusalAsync(res)).Should().Contain("list of options or references");
        }
    }

    [Fact]
    public async Task Has_on_an_array_matches_a_number_or_true_false_element_by_its_text()
    {
        var (_, evt) = await StoreTypesAsync();
        await StoreEntryAsync(evt, new() { ["Title"] = "numbers", ["Tags"] = new List<object> { 5L, true } });
        await StoreEntryAsync(evt, new() { ["Title"] = "text", ["Tags"] = new List<object> { "5" } });
        await StoreEntryAsync(evt, new() { ["Title"] = "fifty", ["Tags"] = new List<object> { 50L } });

        (await TitlesAsync($"/api/public/{evt}?filter[Tags][has]=5")).Should().Equal("numbers", "text");
        (await TitlesAsync($"/api/public/{evt}?filter[Tags][has]=true")).Should().Equal("numbers");
    }

    [Fact]
    public async Task A_saved_query_limits_a_list_reference_to_eq_and_ne()
    {
        var (_, evt) = await StoreTypesAsync();

        using var scope = _fixture.Services.CreateScope();
        var runner = new barakoCMS.Infrastructure.Connectors.QueryRunner(
            scope.ServiceProvider.GetRequiredService<IQuerySession>());

        QueryDefinition Query(string op) => new()
        {
            ContentType = evt,
            Fields = ["Title"],
            Filters = [new QueryFilter { Field = "Speakers", Op = op, Value = Guid.NewGuid().ToString() }],
        };

        (await runner.ValidateAsync(Query("eq"), CancellationToken.None)).Should().BeNull();

        var refused = await runner.ValidateAsync(Query("lt"), CancellationToken.None);
        refused.Should().Contain("list of options or references").And.NotContain("or has",
            "a saved query has no has operator to offer");
    }

    [Fact]
    public async Task Has_on_a_field_that_holds_no_list_is_refused()
    {
        var (_, evt) = await StoreTypesAsync();

        var res = await _fixture.CreateClient().GetAsync($"/api/public/{evt}?filter[Title][has]=talks");

        (await RefusalAsync(res)).Should().Contain("needs a field holding a list");
    }

    private sealed record Item(Dictionary<string, JsonElement> Data);

    private sealed record Page(List<Item> Items, int TotalItems);

    private async Task<List<string>> TitlesAsync(string url)
    {
        var res = await _fixture.CreateClient().GetAsync(url);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var page = await res.Content.ReadFromJsonAsync<Page>();
        page.Should().NotBeNull();
        return page!.Items.Select(i => i.Data["Title"].GetString()!).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    // ---- access conditions -----------------------------------------------------------------

    [Fact]
    public void A_condition_does_not_follow_a_reference_that_holds_a_list()
    {
        var definition = new ContentTypeDefinition
        {
            Name = "enrollment",
            Fields =
            [
                new FieldDefinition { Name = "Class", Type = "reference", ReferenceType = "class" },
                new FieldDefinition { Name = "Classes", Type = "reference", ReferenceType = "class", Multiple = true },
            ],
        };

        ReferenceConditions.ReferenceField(definition, "Class").Should().NotBeNull();
        ReferenceConditions.ReferenceField(definition, "Classes").Should().BeNull(
            "the per-entry check and the list predicate both start here, so both deny");
    }

    [Fact]
    public async Task A_role_condition_through_a_many_valued_reference_is_refused_when_saved()
    {
        var (_, evt) = await StoreTypesAsync();

        object Role(string key) => new
        {
            name = $"MvRef_{Guid.NewGuid():n}",
            description = "a role under test",
            permissions = new[]
            {
                new
                {
                    contentTypeSlug = evt,
                    create = new { enabled = false, conditions = new Dictionary<string, object>() },
                    read = new
                    {
                        enabled = true,
                        conditions = new Dictionary<string, object>
                        {
                            [key] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        },
                    },
                    update = new { enabled = false, conditions = new Dictionary<string, object>() },
                    delete = new { enabled = false, conditions = new Dictionary<string, object>() },
                    transitions = new Dictionary<string, object>(),
                },
            },
            systemCapabilities = Array.Empty<string>(),
        };

        var refused = await _client.PostAsJsonAsync("/api/roles", Role("Speakers.Name"));

        (await RefusalAsync(refused)).Should().Contain("is not a reference field of the");
    }
}
