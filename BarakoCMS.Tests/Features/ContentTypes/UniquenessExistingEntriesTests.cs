using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A rule declared on a type that already holds entries: entries that already share their values
/// are counted and the rule is refused unless forced, and once forced they stay readable and
/// editable while a write that would bring another entry to their values is refused.
/// </summary>
/// <remarks>A tenant per test, so the counts and the lists are this test's entries and no others.</remarks>
[Collection("Sequential")]
public class UniquenessExistingEntriesTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public UniquenessExistingEntriesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rule = "OneHolderPerBadge";

    private static readonly object[] BadgeRule = [new { name = Rule, fields = new[] { "Badge" } }];

    private async Task<HttpClient> AdminOfNewTenantAsync()
    {
        var slug = $"unx-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            session.Store(new User
            {
                Id = userId,
                Username = $"unx-{Guid.NewGuid():n}"[..14],
                Email = $"unx-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(
                roles: ["SuperAdmin", "Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.105.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    /// <summary>A badge register declared with no rule, holding the badges given, one entry each.</summary>
    private static async Task<(string Type, List<Guid> Ids)> RegisterAsync(HttpClient client, params string[] badges)
    {
        var type = $"reg{Guid.NewGuid():n}"[..14];
        var created = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = type,
            displayName = "Register",
            fields = new[]
            {
                new { name = "Badge", displayName = "Badge", type = "string" },
                new { name = "Note", displayName = "Note", type = "string" },
            },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));

        var ids = new List<Guid>();
        foreach (var badge in badges)
        {
            ids.Add(await IdOfAsync(await CreateAsync(client, type, badge, "first")));
        }

        return (type, ids);
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, string badge, string note) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Badge"] = badge, ["Note"] = note },
        }, Ct);

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, string badge, string note) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            id,
            data = new Dictionary<string, object> { ["Badge"] = badge, ["Note"] = note },
        }, Ct);

    private static Task<HttpResponseMessage> SetRulesAsync(HttpClient client, string type, object[] rules, bool force = false) =>
        client.PutAsJsonAsync($"/api/content-types/{type}/uniqueness", new { uniqueness = rules, force }, Ct);

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync(Ct)).Replace("\\u0027", "'");

    [Fact]
    public async Task A_rule_over_entries_that_already_share_a_value_is_refused_without_force_and_nothing_is_stored()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client, "B-1", "B-1", "B-2");

        var refused = await SetRulesAsync(client, type, BadgeRule);
        var body = await BodyAsync(refused);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        body.Should().Contain("2 entries share their values under the rule 'OneHolderPerBadge'");
        body.Should().Contain("force");

        // Not stored: a third entry may still take the shared value.
        (await CreateAsync(client, type, "B-1", "third")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task With_force_the_rule_is_stored_and_the_entries_sharing_a_value_are_listed()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, ids) = await RegisterAsync(client, "B-1", "B-1", "B-2");

        var forced = await SetRulesAsync(client, type, BadgeRule, force: true);
        var body = await BodyAsync(forced);
        forced.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using (var doc = JsonDocument.Parse(body))
        {
            var duplicates = doc.RootElement.GetProperty("duplicates").EnumerateArray().ToList();
            duplicates.Should().HaveCount(1);
            duplicates[0].GetProperty("rule").GetString().Should().Be(Rule);
            duplicates[0].GetProperty("entries").GetInt32().Should().Be(2);
        }

        var listed = await client.GetAsync($"/api/content-types/{type}/uniqueness/{Rule}/duplicates?pageSize=100", Ct);
        var listBody = await BodyAsync(listed);
        listed.StatusCode.Should().Be(HttpStatusCode.OK, listBody);

        using var list = JsonDocument.Parse(listBody);
        var items = list.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
        items.Should().HaveCount(2);
        items.Should().BeEquivalentTo(new[] { ids[0], ids[1] }, "the entry holding B-2 shares nothing");
    }

    [Fact]
    public async Task An_entry_that_shared_its_value_before_the_rule_still_takes_an_edit_that_keeps_it()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, ids) = await RegisterAsync(client, "B-1", "B-1");
        (await SetRulesAsync(client, type, BadgeRule, force: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var edit = await UpdateAsync(client, ids[0], "B-1", "edited");

        edit.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(edit));
    }

    [Fact]
    public async Task Another_entry_cannot_take_a_value_two_entries_already_share()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client, "B-1", "B-1");
        (await SetRulesAsync(client, type, BadgeRule, force: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var third = await CreateAsync(client, type, "B-1", "third");

        third.StatusCode.Should().Be(HttpStatusCode.Conflict, await BodyAsync(third));
    }

    [Fact]
    public async Task A_shared_entry_that_moves_to_a_free_value_cannot_move_back()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, ids) = await RegisterAsync(client, "B-1", "B-1");
        (await SetRulesAsync(client, type, BadgeRule, force: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await UpdateAsync(client, ids[0], "B-3", "moved")).StatusCode.Should().Be(HttpStatusCode.OK);

        var back = await UpdateAsync(client, ids[0], "B-1", "moved back");

        back.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the entry no longer holds B-1, so taking it again is a new claim on a value another entry holds");
    }

    [Fact]
    public async Task A_type_whose_entries_share_nothing_takes_a_rule_without_force()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client, "B-1", "B-2");

        var set = await SetRulesAsync(client, type, BadgeRule);
        var body = await BodyAsync(set);

        set.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("duplicates").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("uniqueness").GetArrayLength().Should().Be(1);

        (await CreateAsync(client, type, "B-2", "taken")).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Taking_the_rules_away_lets_two_entries_hold_a_value_again()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client, "B-1");
        (await SetRulesAsync(client, type, BadgeRule)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreateAsync(client, type, "B-1", "second")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await SetRulesAsync(client, type, [])).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CreateAsync(client, type, "B-1", "second")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_rule_naming_a_state_the_stored_type_does_not_declare_is_refused()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client);

        var response = await SetRulesAsync(client, type, [new { name = Rule, fields = new[] { "Badge" }, whenState = "Open" }]);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(response)).Should().Contain("the type declares no lifecycle");
    }

    [Fact]
    public async Task Raising_the_sensitivity_of_a_field_a_rule_compares_is_refused()
    {
        var client = await AdminOfNewTenantAsync();
        var (type, _) = await RegisterAsync(client);
        (await SetRulesAsync(client, type, BadgeRule)).StatusCode.Should().Be(HttpStatusCode.OK);

        var raise = await client.PutAsJsonAsync(
            $"/api/content-types/{type}/fields/Badge/sensitivity", new { sensitivity = "Sensitive" }, Ct);
        var body = await BodyAsync(raise);

        raise.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("compared by the uniqueness rule 'OneHolderPerBadge'");

        // A field no rule names is raised as before.
        (await client.PutAsJsonAsync(
            $"/api/content-types/{type}/fields/Note/sensitivity", new { sensitivity = "Sensitive" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
