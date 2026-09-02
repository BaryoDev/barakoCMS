using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// The three things the entries list owes the redesign: a version per row, a status filter that
/// knows about Scheduled, and a search over the entry's own data.
/// </summary>
/// <remarks>
/// Every filter here is asserted twice: what it returns and what it leaves out. A status filter that
/// returned everything and a search that matched everything both pass a test that only counts what
/// came back, and this endpoint materialises the whole permitted set, so "it returned my row" is the
/// easiest false positive in the codebase to write.
/// </remarks>
[Collection("Sequential")]
public class EntriesListTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EntriesListTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task AuthenticateAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string NewType() => "el" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Guid> CreateAsync(string type, Dictionary<string, object> data)
    {
        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            ContentType = type,
            Data = data,
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>(
            ApiJson.Options, TestContext.Current.CancellationToken))!.Id;
    }

    private async Task<JsonElement> ListAsync(string query)
    {
        var response = await _client.GetAsync("/api/contents?" + query, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return JsonDocument
            .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.Clone();
    }

    private static List<Guid> IdsOf(JsonElement page) => page.GetProperty("items")
        .EnumerateArray()
        .Select(i => i.GetProperty("id").GetGuid())
        .ToList();

    private async Task ScheduleAsync(Guid id, DateTime? publishAt)
    {
        var response = await _client.PutAsJsonAsync($"/api/contents/{id}/schedule", new
        {
            Id = id,
            ScheduledPublishAt = publishAt,
        }, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private async Task<Content?> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id);
    }

    [Fact]
    public async Task Arming_a_publish_time_moves_a_draft_to_scheduled_and_clearing_it_moves_it_back()
    {
        await AuthenticateAsync();
        var type = NewType();
        var id = await CreateAsync(type, new() { ["Title"] = "pending" });

        (await LoadAsync(id))!.Status.Should().Be(ContentStatus.Draft, "a new entry starts as a draft");

        await ScheduleAsync(id, DateTime.UtcNow.AddDays(3));
        (await LoadAsync(id))!.Status.Should().Be(ContentStatus.Scheduled);

        await ScheduleAsync(id, null);

        var cleared = await LoadAsync(id);
        cleared!.Status.Should().Be(ContentStatus.Draft, "taking the date away leaves an ordinary draft");
        cleared.ScheduledPublishAt.Should().BeNull();
    }

    /// <summary>
    /// The status move is in the history, because it went through an event rather than being derived.
    /// </summary>
    /// <remarks>
    /// This is the difference between the two designs the issue offered, and it is the reason the
    /// more expensive one was taken. Deriving the status inside <c>Apply(ContentScheduled)</c> would
    /// produce the same document and leave the history saying the entry never changed status, which
    /// is what every workflow watching for a transition reads.
    /// </remarks>
    [Fact]
    public async Task Scheduling_records_the_status_change_in_the_history()
    {
        await AuthenticateAsync();
        var type = NewType();
        var id = await CreateAsync(type, new() { ["Title"] = "traceable" });

        await ScheduleAsync(id, DateTime.UtcNow.AddDays(3));

        var response = await _client.GetAsync($"/api/contents/{id}/history", TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var entries = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.GetProperty("items").EnumerateArray().ToList();

        entries.Should().NotBeEmpty("an empty history would satisfy every assertion below by having nothing in it");

        entries.Should().Contain(
            e => e.GetProperty("changeType").GetString() == "StatusChanged"
              && e.GetProperty("status").GetString() == "Scheduled",
            "a status that moved with nothing in the history behind it is a status nobody can audit");

        entries.Should().Contain(
            e => e.GetProperty("changeType").GetString() == "Scheduled"
              && e.GetProperty("scheduledPublishAt").ValueKind != JsonValueKind.Null,
            "and the date it was armed with is its own entry, so a replay recovers both");
    }

    [Fact]
    public async Task A_published_entry_with_an_unpublish_time_stays_published()
    {
        await AuthenticateAsync();
        var type = NewType();
        var id = await CreateAsync(type, new() { ["Title"] = "live" });

        var published = await _client.PutAsJsonAsync($"/api/contents/{id}/status",
            new barakoCMS.Features.Content.ChangeStatus.Request { Id = id, NewStatus = ContentStatus.Published },
            TestContext.Current.CancellationToken);
        published.IsSuccessStatusCode.Should().BeTrue(
            await published.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var response = await _client.PutAsJsonAsync($"/api/contents/{id}/schedule", new
        {
            Id = id,
            ScheduledUnpublishAt = DateTime.UtcNow.AddDays(7),
        }, TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue();

        var stored = await LoadAsync(id);
        stored!.Status.Should().Be(ContentStatus.Published,
            "an entry that will come down on Friday is published until Friday");
        stored.ScheduledUnpublishAt.Should().NotBeNull("and the date it comes down is still armed");
    }

    [Fact]
    public async Task The_status_filter_returns_that_status_and_leaves_the_others_out()
    {
        await AuthenticateAsync();
        var type = NewType();

        var draft = await CreateAsync(type, new() { ["Title"] = "a draft" });
        var scheduled = await CreateAsync(type, new() { ["Title"] = "a scheduled one" });
        await ScheduleAsync(scheduled, DateTime.UtcNow.AddDays(2));

        var scheduledPage = IdsOf(await ListAsync($"contentType={type}&status=Scheduled&pageSize=100"));
        var draftPage = IdsOf(await ListAsync($"contentType={type}&status=Draft&pageSize=100"));
        var everything = IdsOf(await ListAsync($"contentType={type}&pageSize=100"));

        scheduledPage.Should().Contain(scheduled);
        scheduledPage.Should().NotContain(draft, "the filter has to leave something out or it is not filtering");

        draftPage.Should().Contain(draft);
        draftPage.Should().NotContain(scheduled);

        everything.Should().Contain(new[] { draft, scheduled },
            "and with no filter both come back, or the two above prove nothing about the filter");
    }

    /// <summary>
    /// Search reaches a value the anonymous delivery search deliberately cannot.
    /// </summary>
    /// <remarks>
    /// The obvious implementation is the derived <c>SearchText</c>, which already exists and is
    /// already indexed by the public search. It holds only the values of Public fields, so an
    /// administrator searching a reference number that lives in a Sensitive field would get an empty
    /// page and no way to tell that from the entry not existing. This asserts the difference rather
    /// than describing it.
    /// </remarks>
    [Fact]
    public async Task Search_matches_a_value_in_a_field_that_is_not_public()
    {
        await AuthenticateAsync();
        var type = NewType();

        var created = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Entries list probe",
            fields = new object[]
            {
                new { name = "Title", type = "text", sensitivity = "Public" },
                new { name = "Reference", type = "text", sensitivity = "Sensitive" },
            },
        }, TestContext.Current.CancellationToken);
        created.IsSuccessStatusCode.Should().BeTrue(
            await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var needle = "zq" + Guid.NewGuid().ToString("N")[..8];
        var match = await CreateAsync(type, new() { ["Title"] = "nothing here", ["Reference"] = needle });
        var other = await CreateAsync(type, new() { ["Title"] = "nothing here either", ["Reference"] = "no" });

        var found = IdsOf(await ListAsync($"contentType={type}&search={needle}&pageSize=100"));

        found.Should().Contain(match, "an admin searching a Sensitive value is the case this surface exists for");
        found.Should().NotContain(other, "and the search has to exclude, or it is matching everything");
    }

    [Fact]
    public async Task Search_matches_values_and_not_field_names()
    {
        await AuthenticateAsync();
        var type = NewType();

        var id = await CreateAsync(type, new() { ["Title"] = "an ordinary entry" });

        var byValue = IdsOf(await ListAsync($"contentType={type}&search=ordinary&pageSize=100"));
        var byFieldName = IdsOf(await ListAsync($"contentType={type}&search=Title&pageSize=100"));

        byValue.Should().Contain(id, "the value is what a person is looking for");
        byFieldName.Should().NotContain(id,
            "matching field names would return every entry of every type that happens to have a Title");
    }

    [Fact]
    public async Task A_wildcard_in_the_search_term_is_a_character_and_not_a_wildcard()
    {
        await AuthenticateAsync();
        var type = NewType();

        var literal = await CreateAsync(type, new() { ["Title"] = "discount 50% off" });
        var decoy = await CreateAsync(type, new() { ["Title"] = "50 items in stock" });

        var found = IdsOf(await ListAsync($"contentType={type}&search={Uri.EscapeDataString("50%")}&pageSize=100"));

        found.Should().Contain(literal);
        found.Should().NotContain(decoy,
            "an unescaped percent is a LIKE wildcard, so this would match every entry containing 50");
    }

    [Fact]
    public async Task Every_row_carries_the_version_the_single_item_endpoint_reports()
    {
        await AuthenticateAsync();
        var type = NewType();
        var id = await CreateAsync(type, new() { ["Title"] = "v1" });

        var updated = await _client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            Id = id,
            Data = new Dictionary<string, object> { ["Title"] = "v2" },
            Version = 1,
        }, TestContext.Current.CancellationToken);
        updated.IsSuccessStatusCode.Should().BeTrue(
            await updated.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var single = JsonDocument.Parse(await (await _client.GetAsync(
            $"/api/contents/{id}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var expected = single.RootElement.GetProperty("version").GetInt64();

        expected.Should().BeGreaterThan(1,
            "the update has to have moved the version, or this compares two copies of the same number");

        var row = (await ListAsync($"contentType={type}&pageSize=100")).GetProperty("items")
            .EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == id);

        row.GetProperty("version").GetInt64().Should().Be(expected,
            "the column exists so the table can show what the detail view would");
    }
}
