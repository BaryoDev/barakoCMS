using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// <c>POST /api/roles/preview</c>: which entries a user would see under rules that are not saved.
/// </summary>
/// <remarks>
/// The agreement test is the one that matters. A preview that disagrees with what the saved role
/// does is worse than none, because an admin acts on it.
/// </remarks>
[Collection("Integration1")]
public class RolePreviewTests
{
    private readonly IntegrationTestFixture _fixture;

    public RolePreviewTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, object> OwnRecords = new()
    {
        ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
    };

    // ---- setup ----

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> TypeAsync()
    {
        var type = $"preview{Guid.NewGuid().ToString("n")[..10]}";
        await StoreAsync(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Grade", DisplayName = "Grade", Type = "string" },
                new FieldDefinition { Name = "Owner", DisplayName = "Owner", Type = "string" },
            ],
        });
        return type;
    }

    private async Task<Guid> UserAsync(params Guid[] roleIds)
    {
        var id = Guid.NewGuid();
        await StoreAsync(new User
        {
            Id = id, Username = $"preview-{id:n}", Email = $"preview-{id:n}@example.com", RoleIds = roleIds.ToList(),
        });
        return id;
    }

    private async Task<Guid> EntryAsync(string type, string title, Guid createdBy, bool withOwnerField = false)
    {
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object> { ["Title"] = title, ["Grade"] = "A" };
        if (withOwnerField) data["Owner"] = "someone";
        await StoreAsync(new Content
        {
            Id = id, ContentType = type, Data = data, CreatedBy = createdBy, Status = ContentStatus.Published,
        });
        return id;
    }

    private static object Rule(bool enabled, Dictionary<string, object>? conditions = null, string[]? readable = null) => new
    {
        enabled,
        conditions,
        readableFields = readable,
    };

    private static object[] ReadRule(string type, Dictionary<string, object>? conditions = null, string[]? readable = null) =>
    [
        new
        {
            contentTypeSlug = type,
            create = new { enabled = false },
            read = Rule(true, conditions, readable),
            update = new { enabled = false },
            delete = new { enabled = false },
        },
    ];

    private async Task<JsonElement> PreviewAsync(
        HttpClient client, Guid userId, string type, object permissions, int? take = null)
    {
        var response = await client.PostAsJsonAsync("/api/roles/preview", new { userId, contentType = type, permissions, take }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static JsonElement ItemFor(JsonElement result, Guid id) =>
        result.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ---- one test per reason ----

    [Fact]
    public async Task An_entry_of_a_type_the_draft_has_no_read_rule_for_says_no_read_rule()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Alpha", user);

        var withNoRule = await PreviewAsync(admin, user, type, Array.Empty<object>());
        var disabled = await PreviewAsync(admin, user, type, new[]
        {
            new { contentTypeSlug = type, read = new { enabled = false } },
        });

        foreach (var result in new[] { withNoRule, disabled })
        {
            var item = ItemFor(result, entry);
            item.GetProperty("visible").GetBoolean().Should().BeFalse();
            Text(item, "reason").Should().Be("no read rule");
            item.GetProperty("title").GetString().Should().Be("Alpha");
        }
    }

    [Fact]
    public async Task An_entry_a_condition_refuses_says_condition_not_met_and_names_the_condition()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        var theirs = await EntryAsync(type, "Mine", user);
        var someoneElses = await EntryAsync(type, "Other", Guid.NewGuid());

        var result = await PreviewAsync(admin, user, type, ReadRule(type, OwnRecords));

        ItemFor(result, theirs).GetProperty("visible").GetBoolean().Should().BeTrue();
        var hidden = ItemFor(result, someoneElses);
        hidden.GetProperty("visible").GetBoolean().Should().BeFalse();
        Text(hidden, "reason").Should().Be("condition not met");
        Text(hidden, "condition").Should().Contain("$createdBy");
    }

    [Fact]
    public async Task An_entry_whose_fields_the_rule_allows_none_of_says_field_rules_hide_it()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        var without = await EntryAsync(type, "No owner field", user);
        var with = await EntryAsync(type, "Has owner field", user, withOwnerField: true);

        var result = await PreviewAsync(admin, user, type, ReadRule(type, readable: ["Owner"]));

        var hidden = ItemFor(result, without);
        hidden.GetProperty("visible").GetBoolean().Should().BeFalse();
        Text(hidden, "reason").Should().Be("field rules hide it");

        var shown = ItemFor(result, with);
        shown.GetProperty("visible").GetBoolean().Should().BeTrue();
        shown.GetProperty("readableFields").EnumerateArray().Select(f => f.GetString())
            .Should().BeEquivalentTo(["Owner"]);
    }

    [Fact]
    public async Task A_visible_entry_under_a_rule_with_no_field_set_reports_no_set()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Open", user);

        var item = ItemFor(await PreviewAsync(admin, user, type, ReadRule(type)), entry);

        item.GetProperty("visible").GetBoolean().Should().BeTrue();
        Text(item, "reason").Should().BeNull();
        item.TryGetProperty("readableFields", out var fields).Should().BeTrue();
        fields.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- agreement with the saved role ----

    [Fact]
    public async Task The_preview_agrees_with_the_saved_role_in_both_directions()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var other = await TypeAsync();
        var roleId = Guid.NewGuid();
        var holder = await UserAsync(roleId);

        // The rule asks for entries the holder created, so the holder owns some and not others.
        var entries = new List<Guid>
        {
            await EntryAsync(type, "Own 1", holder),
            await EntryAsync(type, "Own 2", holder),
            await EntryAsync(type, "Foreign 1", Guid.NewGuid()),
            await EntryAsync(type, "Foreign 2", Guid.NewGuid()),
        };
        var elsewhere = await EntryAsync(other, "Other type", holder);

        // The same rules the request carries, saved as a role the user holds.
        var role = new Role
        {
            Id = roleId,
            Name = $"Agree_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["$createdBy"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        },
                    },
                },
            ],
        };
        await StoreAsync(role);

        var previewOfHolder = await PreviewAsync(admin, holder, type, ReadRule(type, OwnRecords));
        var previewOfOther = await PreviewAsync(admin, holder, other, ReadRule(type, OwnRecords));

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var stored = (await session.LoadAsync<User>(holder, Ct))!;

        foreach (var id in entries)
        {
            var entry = (await session.LoadAsync<Content>(id, Ct))!;
            var saved = await resolver.CanPerformActionAsync(stored, type, "read", entry, Ct);
            ItemFor(previewOfHolder, id).GetProperty("visible").GetBoolean().Should().Be(saved, $"entry {entry.Data["Title"]}");
        }

        // Both directions: some entries are granted and some refused, so the test can fail either way.
        entries.Select(id => ItemFor(previewOfHolder, id).GetProperty("visible").GetBoolean())
            .Should().Contain(true).And.Contain(false);

        // The draft names no rule for this type, so it is refused, as the saved role refuses it.
        var otherEntry = (await session.LoadAsync<Content>(elsewhere, Ct))!;
        (await resolver.CanPerformActionAsync(stored, other, "read", otherEntry, Ct)).Should().BeFalse();
        ItemFor(previewOfOther, elsewhere).GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Current_user_is_the_previewed_user_and_not_the_caller()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var previewed = await UserAsync();
        var callersId = Guid.Parse(
            new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
                .ReadJwtToken(await _fixture.StoredUserTokenAsync("SuperAdmin"))
                .Claims.First(c => c.Type == "UserId").Value);

        var byPreviewed = await EntryAsync(type, "By previewed", previewed);
        var byCaller = await EntryAsync(type, "By caller", callersId);

        var result = await PreviewAsync(admin, previewed, type, ReadRule(type, OwnRecords));

        ItemFor(result, byPreviewed).GetProperty("visible").GetBoolean().Should().BeTrue();
        ItemFor(result, byCaller).GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    // ---- what the caller may see ----

    private async Task<HttpClient> ManageRolesCallerAsync(string? type, List<string>? readable = null)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Previewer_{Guid.NewGuid():n}",
            SystemCapabilities = [SystemCapabilities.ManageRoles],
            Permissions = type is null
                ? []
                : [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true, ReadableFields = readable } }],
        };
        var userId = await UserAsync(role.Id);
        await StoreAsync(role);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [role.Name], userId: userId.ToString()));
        return client;
    }

    [Fact]
    public async Task A_caller_with_manage_roles_but_no_read_on_the_type_gets_those_entries_left_out()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Hidden from the caller", user);

        var blind = await PreviewAsync(await ManageRolesCallerAsync(null), user, type, ReadRule(type));
        blind.GetProperty("items").GetArrayLength().Should().Be(0, "the preview must not list what the caller cannot open");

        // The same request from a caller who may read the type does list it.
        var sighted = await PreviewAsync(await ManageRolesCallerAsync(type), user, type, ReadRule(type));
        ItemFor(sighted, entry).GetProperty("visible").GetBoolean().Should().BeTrue();
    }

    // ---- the draft may not ask about fields the caller cannot read ----

    private static readonly Dictionary<string, object> GradeIsA = new()
    {
        ["Grade"] = new Dictionary<string, object> { ["_eq"] = "A" },
    };

    [Fact]
    public async Task A_condition_on_a_field_the_caller_may_not_read_is_refused_with_400_naming_it()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        await EntryAsync(type, "Row", user);

        var caller = await ManageRolesCallerAsync(type, ["Title"]);
        var response = await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type, GradeIsA) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Grade");
    }

    [Fact]
    public async Task A_condition_on_a_field_the_caller_may_read_still_previews()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Row", user);

        var caller = await ManageRolesCallerAsync(type, ["Title", "Grade"]);
        var result = await PreviewAsync(caller, user, type, ReadRule(type, GradeIsA));

        ItemFor(result, entry).GetProperty("visible").GetBoolean().Should().BeTrue();
    }

    // ---- tenant ----

    [Fact]
    public async Task A_user_of_another_tenant_answers_404()
    {
        var home = $"home{Guid.NewGuid():n}"[..14];
        var away = $"away{Guid.NewGuid():n}"[..14];
        // Content types are stored per tenant, so this one lives in the caller's tenant.
        var type = $"preview{Guid.NewGuid().ToString("n")[..10]}";

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"TenantPreviewer_{Guid.NewGuid():n}",
            SystemCapabilities = [SystemCapabilities.ManageRoles],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var caller = await UserAsync();
        var local = await UserAsync();
        var foreigner = await UserAsync();
        var suspended = await UserAsync();

        await StoreAsync(
            role,
            new Tenant { Id = Guid.NewGuid(), Slug = home, Name = home },
            new Tenant { Id = Guid.NewGuid(), Slug = away, Name = away },
            new Membership { Id = Guid.NewGuid(), UserId = caller, TenantSlug = home, Status = MembershipStatus.Active, RoleIds = [role.Id] },
            new Membership { Id = Guid.NewGuid(), UserId = local, TenantSlug = home, Status = MembershipStatus.Active, RoleIds = [] },
            new Membership { Id = Guid.NewGuid(), UserId = foreigner, TenantSlug = away, Status = MembershipStatus.Active, RoleIds = [] },
            new Membership { Id = Guid.NewGuid(), UserId = suspended, TenantSlug = home, Status = MembershipStatus.Suspended, RoleIds = [] });

        using (var scope = _fixture.Services.CreateScope())
        {
            await using var inHome = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession(home);
            inHome.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = type, DisplayName = type,
                Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
            });
            await inHome.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: [role.Name], userId: caller.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = home }));
        client.DefaultRequestHeaders.Add("X-Tenant", home);

        var refused = await client.PostAsJsonAsync("/api/roles/preview",
            new { userId = foreigner, contentType = type, permissions = ReadRule(type) }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Role resolution counts only Active memberships, so a suspended member is not previewed.
        var held = await client.PostAsJsonAsync("/api/roles/preview",
            new { userId = suspended, contentType = type, permissions = ReadRule(type) }, Ct);
        held.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var allowed = await client.PostAsJsonAsync("/api/roles/preview",
            new { userId = local, contentType = type, permissions = ReadRule(type) }, Ct);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync(Ct));
    }

    // ---- nothing is saved ----

    [Fact]
    public async Task A_preview_saves_nothing_and_the_real_read_route_still_refuses_the_user()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Stays closed", user);

        // The real resolver caches item-less decisions, so ask once to warm it, then compare.
        async Task<bool> RealReadAsync()
        {
            using var scope = _fixture.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var stored = (await session.LoadAsync<User>(user, Ct))!;
            return await scope.ServiceProvider.GetRequiredService<IPermissionResolver>()
                .CanPerformActionAsync(stored, type, "read", null, Ct);
        }

        var before = await RealReadAsync();

        var preview = await PreviewAsync(admin, user, type, ReadRule(type));
        ItemFor(preview, entry).GetProperty("visible").GetBoolean().Should().BeTrue();

        (await RealReadAsync()).Should().Be(before, "the draft must not reach the cached resolver");
        before.Should().BeFalse();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

            // The draft is named "preview", so a saved one would show up under that name.
            (await session.Query<Role>().Where(r => r.Name == "preview").CountAsync(Ct)).Should().Be(0);
            (await session.LoadAsync<User>(user, Ct))!.RoleIds.Should().BeEmpty();
        }

        // The user holds no role, so the real route refuses the entry the preview showed as visible.
        var asUser = _fixture.CreateClient();
        asUser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [], userId: user.ToString()));
        var read = await asUser.GetAsync($"/api/contents/{entry}", Ct);
        read.IsSuccessStatusCode.Should().BeFalse("the preview must not have granted the user anything");
    }

    // ---- take ----

    [Fact]
    public async Task Take_defaults_to_20_and_is_clamped_to_1_through_50()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();

        var entries = Enumerable.Range(0, 60).Select(i => new Content
        {
            Id = Guid.NewGuid(), ContentType = type, CreatedBy = user, Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = $"Entry {i}" },
            CreatedAt = DateTime.UtcNow.AddSeconds(i),
        }).Cast<object>().ToArray();
        await StoreAsync(entries);

        async Task<int> CountAsync(int? take) =>
            (await PreviewAsync(admin, user, type, ReadRule(type), take)).GetProperty("items").GetArrayLength();

        (await CountAsync(null)).Should().Be(20);
        (await CountAsync(5)).Should().Be(5);
        (await CountAsync(500)).Should().Be(50);
        (await CountAsync(0)).Should().Be(1);
        (await CountAsync(-3)).Should().Be(1);
    }

    // ---- the gate ----

    [Fact]
    public async Task An_api_key_is_refused()
    {
        var type = await TypeAsync();
        var user = await UserAsync();

        // A key whose owner is a SuperAdmin, so the refusal is the key's and not the owner's.
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        var ownerToken = await _fixture.StoredUserTokenAsync("SuperAdmin");
        var ownerId = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(ownerToken).Claims.First(c => c.Type == "UserId").Value);
        await StoreAsync(new ApiKey
        {
            Id = Guid.NewGuid(), Name = "preview", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
            UserId = ownerId, TenantSlug = Tenant.DefaultSlug, Scopes = ["*"],
        });

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        var response = await client.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
