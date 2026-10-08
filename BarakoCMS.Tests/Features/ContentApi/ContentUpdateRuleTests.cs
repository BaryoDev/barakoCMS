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

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// What an update is checked against: the rule granting it holds for the entry before and after the
/// write, and a field the caller did not send cannot fail the write or show up in its answer.
/// </summary>
[Collection("Sequential")]
public class ContentUpdateRuleTests
{
    private const string StaleTier = "GOLD_4417";

    private readonly IntegrationTestFixture _factory;

    public ContentUpdateRuleTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PermissionRule BranchA() => new()
    {
        Enabled = true,
        Conditions = new Dictionary<string, object>
        {
            ["Branch"] = new Dictionary<string, object> { ["_eq"] = "A" },
        },
    };

    private async Task<string> StoreTypeAsync(params FieldDefinition[] fields)
    {
        var type = $"cur{Guid.NewGuid():N}"[..16];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }, .. fields],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    /// <summary>A stored user holding one stored role with these rules on the type.</summary>
    private async Task<HttpClient> MemberAsync(
        string type, PermissionRule update, PermissionRule? create = null, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Member {Guid.NewGuid():N}",
            SystemCapabilities = capabilities.ToList(),
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Create = create ?? new PermissionRule(),
                    Update = update,
                },
            ],
        };
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(new User
            {
                Id = userId,
                Username = $"cur-{userId:n}",
                Email = $"cur-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken([role.Name], userId.ToString()));
        return client;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string type, Dictionary<string, object> data)
    {
        var res = await client.PostAsJsonAsync("/api/contents", new { contentType = type, data }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, Dictionary<string, object> data) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new { id, data }, Ct);

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<Content>(id, Ct);
        stored.Should().NotBeNull("the entry {0} exists", id);
        return stored!;
    }

    private static string? Value(Content entry, string field) =>
        entry.Data.TryGetValue(field, out var value) ? value?.ToString() : null;

    // ---- a stored value the caller cannot see ------------------------------------------------

    [Fact]
    public async Task A_stale_hidden_choice_does_not_block_an_edit_of_another_field_or_appear_in_the_answer()
    {
        var type = await StoreTypeAsync(new FieldDefinition
        {
            Name = "Tier", DisplayName = "Tier", Type = "choice", Sensitivity = SensitivityLevel.Hidden,
            Options = [new() { Value = StaleTier, Label = "Gold" }, new() { Value = "SILVER", Label = "Silver" }],
        });
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, new() { ["Title"] = "before", ["Tier"] = StaleTier });

        // The option the entry holds is removed, which leaves the stored value outside the options.
        var forced = await admin.PutAsJsonAsync($"/api/content-types/{type}/fields/Tier/options", new
        {
            options = new[] { new { value = "SILVER", label = "Silver" } },
            force = true,
        }, Ct);
        forced.StatusCode.Should().Be(HttpStatusCode.OK, await forced.Content.ReadAsStringAsync(Ct));

        var member = await MemberAsync(type, update: new PermissionRule { Enabled = true });
        var res = await UpdateAsync(member, id, new() { ["Title"] = "after" });
        var body = await res.Content.ReadAsStringAsync(Ct);

        body.Should().NotContain(StaleTier, "the caller may not see the Tier field");
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var stored = await LoadAsync(id);
        Value(stored, "Title").Should().Be("after");
        Value(stored, "Tier").Should().Be(StaleTier, "a field the caller may not see is kept as stored");
    }

    [Fact]
    public async Task An_email_stored_in_a_form_now_refused_is_kept_on_an_edit_but_a_new_one_is_checked()
    {
        var type = await StoreTypeAsync(new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" });
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, new() { ["Title"] = "t", ["Email"] = "ana@example.com" });

        const string Legacy = "Ana <ana@example.com>";
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var entry = await session.LoadAsync<Content>(id, Ct);
            entry!.Data["Email"] = Legacy;
            session.Store(entry);
            await session.SaveChangesAsync(Ct);
        }

        var kept = await UpdateAsync(admin, id, new() { ["Title"] = "edited", ["Email"] = Legacy });
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Email").Should().Be(Legacy);

        var refused = await UpdateAsync(admin, id, new() { ["Title"] = "edited", ["Email"] = "Bo <bo@example.com>" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
    }

    // ---- one field sent under two spellings ----------------------------------------------------

    private async Task<string> StoreSpellingTypeAsync() => await StoreTypeAsync(
        new FieldDefinition { Name = "slug", DisplayName = "Slug", Type = "slug" },
        new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" },
        new FieldDefinition
        {
            Name = "Tier", DisplayName = "Tier", Type = "choice",
            Options = [new() { Value = "GOLD", Label = "Gold" }, new() { Value = "SILVER", Label = "Silver" }],
        });

    /// <summary>A good value under one spelling first, then a bad one under another.</summary>
    private static Dictionary<string, object> TwoSpellings(string field, string good, string bad, string slug = "one") =>
        field == "Email"
            ? new() { ["slug"] = slug, ["Title"] = "t", ["eMAIL"] = good, ["Email"] = bad }
            : new() { ["slug"] = slug, ["Title"] = "t", ["tier"] = good, ["Tier"] = bad };

    [Theory]
    [InlineData("Email", "ok@example.com", "Ana <ana@example.com>")]
    [InlineData("Tier", "GOLD", "PLATINUM")]
    public async Task A_field_sent_under_two_spellings_is_refused_on_create(string field, string good, string bad)
    {
        var type = await StoreSpellingTypeAsync();
        var admin = await SuperAdminAsync();

        var res = await admin.PostAsJsonAsync("/api/contents",
            new { contentType = type, data = TwoSpellings(field, good, bad) }, Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("more than once");
    }

    [Theory]
    [InlineData("Email", "ok@example.com", "Ana <ana@example.com>")]
    [InlineData("Tier", "GOLD", "PLATINUM")]
    public async Task A_field_sent_under_two_spellings_is_refused_on_update(string field, string good, string bad)
    {
        var type = await StoreSpellingTypeAsync();
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, new() { ["slug"] = "one", ["Title"] = "t" });

        var res = await UpdateAsync(admin, id, TwoSpellings(field, good, bad));
        var body = await res.Content.ReadAsStringAsync(Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("more than once");
        (await LoadAsync(id)).Data.Keys.Should().NotContain(k => k.Equals(field, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_field_sent_under_two_spellings_is_refused_in_a_push_and_an_import()
    {
        var type = await StoreSpellingTypeAsync();
        var admin = await SuperAdminAsync();

        var pushed = await admin.PostAsJsonAsync($"/api/collections/{type}/push",
            new { entries = new[] { TwoSpellings("Email", "ok@example.com", "Ana <ana@example.com>") } }, Ct);
        var pushBody = await pushed.Content.ReadAsStringAsync(Ct);
        pushed.StatusCode.Should().Be(HttpStatusCode.BadRequest, pushBody);
        pushBody.Should().Contain("more than once");

        var imported = await admin.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[] { TwoSpellings("Tier", "GOLD", "PLATINUM", slug: "two") },
        }, Ct);
        var importBody = await imported.Content.ReadAsStringAsync(Ct);
        importBody.Should().Contain("more than once");
        using (var doc = JsonDocument.Parse(importBody))
            doc.RootElement.GetProperty("created").GetInt32().Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct))
            .Should().BeEmpty("nothing sent under two spellings was stored");
    }

    // ---- a row rule holds before and after the write -------------------------------------------

    [Fact]
    public async Task A_member_limited_to_branch_A_cannot_update_an_entry_out_of_branch_A()
    {
        var type = await StoreTypeAsync(new FieldDefinition { Name = "Branch", DisplayName = "Branch", Type = "string" });
        var id = await CreateAsync(await SuperAdminAsync(), type, new() { ["Title"] = "t", ["Branch"] = "A" });
        var member = await MemberAsync(type, update: BranchA());

        var moved = await UpdateAsync(member, id, new() { ["Title"] = "moved", ["Branch"] = "B" });
        moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Branch").Should().Be("A", "a refused update writes nothing");

        var kept = await UpdateAsync(member, id, new() { ["Title"] = "kept", ["Branch"] = "A" });
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Title").Should().Be("kept");
    }

    [Fact]
    public async Task A_push_cannot_move_an_entry_out_of_the_branch_the_member_is_limited_to()
    {
        var type = await StoreTypeAsync(
            new FieldDefinition { Name = "slug", DisplayName = "Slug", Type = "slug", IsRequired = true },
            new FieldDefinition { Name = "Branch", DisplayName = "Branch", Type = "string" });
        var id = await CreateAsync(await SuperAdminAsync(), type, new() { ["slug"] = "one", ["Title"] = "t", ["Branch"] = "A" });
        var member = await MemberAsync(type, update: BranchA());

        var moved = await member.PostAsJsonAsync($"/api/collections/{type}/push", new
        {
            status = "Draft",
            entries = new[] { new Dictionary<string, object> { ["slug"] = "one", ["Title"] = "moved", ["Branch"] = "B" } },
        }, Ct);
        moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Branch").Should().Be("A");

        var kept = await member.PostAsJsonAsync($"/api/collections/{type}/push", new
        {
            status = "Draft",
            entries = new[] { new Dictionary<string, object> { ["slug"] = "one", ["Title"] = "kept", ["Branch"] = "A" } },
        }, Ct);
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Title").Should().Be("kept");
    }

    [Fact]
    public async Task A_transition_carrying_values_cannot_move_an_entry_out_of_the_branch_its_rule_is_limited_to()
    {
        var type = $"cur{Guid.NewGuid():N}"[..16];
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                    new FieldDefinition { Name = "Branch", DisplayName = "Branch", Type = "string" },
                ],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Open", "Closed"],
                    InitialState = "Open",
                    Transitions = [new StateTransition { Name = "Close", From = "Open", To = "Closed", OptionalFields = ["Branch"] }],
                },
            });
            await session.SaveChangesAsync(Ct);
        }

        var admin = await SuperAdminAsync();
        var moving = await CreateAsync(admin, type, new() { ["Title"] = "t", ["Branch"] = "A" });
        var staying = await CreateAsync(admin, type, new() { ["Title"] = "t", ["Branch"] = "A" });

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Closer {Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Transitions = new(StringComparer.OrdinalIgnoreCase) { ["Close"] = BranchA() },
                },
            ],
        };
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(role);
            session.Store(new User { Id = userId, Username = $"cur-{userId:n}", Email = $"cur-{userId:n}@example.com", RoleIds = [role.Id] });
            await session.SaveChangesAsync(Ct);
        }

        var closer = _factory.CreateClient();
        closer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken([role.Name], userId.ToString()));

        var moved = await closer.PutAsJsonAsync($"/api/contents/{moving}/status",
            new { id = moving, transition = "Close", data = new Dictionary<string, object> { ["Branch"] = "B" } }, Ct);
        moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync(Ct));
        var refused = await LoadAsync(moving);
        Value(refused, "Branch").Should().Be("A");
        refused.LifecycleState.Should().Be("Open", "a refused transition changes nothing");

        var kept = await closer.PutAsJsonAsync($"/api/contents/{staying}/status",
            new { id = staying, transition = "Close", data = new Dictionary<string, object> { ["Branch"] = "A" } }, Ct);
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(Ct));
        (await LoadAsync(staying)).LifecycleState.Should().Be("Closed");
    }

    [Fact]
    public async Task A_rollback_cannot_restore_an_entry_to_a_branch_the_member_is_not_limited_to()
    {
        var type = await StoreTypeAsync(new FieldDefinition { Name = "Branch", DisplayName = "Branch", Type = "string" });
        var admin = await SuperAdminAsync();
        var id = await CreateAsync(admin, type, new() { ["Title"] = "in B", ["Branch"] = "B" });
        var toA = await UpdateAsync(admin, id, new() { ["Title"] = "in A", ["Branch"] = "A" });
        toA.StatusCode.Should().Be(HttpStatusCode.OK, await toA.Content.ReadAsStringAsync(Ct));

        Guid createdVersion;
        Guid updatedVersion;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var events = await session.Events.FetchStreamAsync(id, token: Ct);
            createdVersion = events.First(e => e.Data is ContentCreated).Id;
            updatedVersion = events.Last(e => e.Data is ContentUpdated).Id;
        }

        var member = await MemberAsync(type, update: BranchA(), create: null, SystemCapabilities.RollbackContent);

        var moved = await member.PostAsync($"/api/contents/{id}/rollback/{createdVersion}", null, Ct);
        moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync(Ct));
        Value(await LoadAsync(id), "Branch").Should().Be("A");

        var kept = await member.PostAsync($"/api/contents/{id}/rollback/{updatedVersion}", null, Ct);
        kept.StatusCode.Should().Be(HttpStatusCode.OK, await kept.Content.ReadAsStringAsync(Ct));
    }
}
