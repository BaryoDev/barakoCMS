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

    private Task<HttpClient> SuperAdminAsync() =>
        _fixture.CreateClientAsync("SuperAdmin");

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> TypeAsync(bool branchScoped = false, SensitivityLevel gradeSensitivity = SensitivityLevel.Public, List<string>? gradeVisibleTo = null)
    {
        var type = $"preview{Guid.NewGuid().ToString("n")[..10]}";
        await StoreAsync(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, BranchScoped = branchScoped,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Grade", DisplayName = "Grade", Type = "string", Sensitivity = gradeSensitivity, VisibleToRoles = gradeVisibleTo },
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

    private async Task<Guid> EntryAsync(
        string type, string title, Guid createdBy, bool withOwnerField = false, Guid? branchId = null, string grade = "A")
    {
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object> { ["Title"] = title, ["Grade"] = grade };
        if (withOwnerField) data["Owner"] = "someone";
        await StoreAsync(new Content
        {
            Id = id, ContentType = type, Data = data, CreatedBy = createdBy, Status = ContentStatus.Published,
            BranchId = branchId,
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
        HttpClient client, Guid userId, string type, object permissions, int? take = null,
        string[]? systemCapabilities = null, string? name = null, Guid? roleId = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/roles/preview", new { userId, contentType = type, permissions, take, systemCapabilities, name, roleId }, Ct);
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

    // ---- branch scope and sensitivity of the previewed user ----

    private async Task<Guid> MemberOfBranchesAsync(params Guid[] branches)
    {
        var user = await UserAsync();
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = user, TenantSlug = Tenant.DefaultSlug, Status = MembershipStatus.Active,
            RoleIds = [], Profile = new Dictionary<string, string> { ["branches"] = string.Join(",", branches) },
        });
        return user;
    }

    [Fact]
    public async Task An_entry_on_a_branch_the_previewed_user_does_not_hold_says_outside_branch_scope()
    {
        var type = await TypeAsync(branchScoped: true);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var previewed = await MemberOfBranchesAsync(a);
        var onA = await EntryAsync(type, "On A", previewed, branchId: a);
        var onB = await EntryAsync(type, "On B", previewed, branchId: b);

        // The caller holds both branches, so both entries are theirs to see and neither is left out.
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"BranchPreviewer_{Guid.NewGuid():n}",
            SystemCapabilities = [SystemCapabilities.ManageRoles],
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }],
        };
        var callerId = await MemberOfBranchesAsync(a, b);
        await StoreAsync(role);
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var caller = (await session.LoadAsync<User>(callerId, Ct))!;
            caller.RoleIds = [role.Id];
            session.Store(caller);
            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [role.Name], userId: callerId.ToString()));

        var result = await PreviewAsync(client, previewed, type, ReadRule(type));

        ItemFor(result, onA).GetProperty("visible").GetBoolean().Should().BeTrue();
        var hidden = ItemFor(result, onB);
        hidden.GetProperty("visible").GetBoolean().Should().BeFalse();
        Text(hidden, "reason").Should().Be("outside branch scope");
    }

    [Fact]
    public async Task A_field_that_is_sensitive_for_the_user_is_not_readable_and_a_condition_on_it_agrees_with_the_real_list()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive);
        var roleId = Guid.NewGuid();
        var holder = await UserAsync(roleId);
        var gradeA = await EntryAsync(type, "Grade A", holder, grade: "A");
        var gradeB = await EntryAsync(type, "Grade B", holder, grade: "B");

        // The same rule, saved as a role the user holds, so the real list can answer for them.
        await StoreAsync(new Role
        {
            Id = roleId, Name = $"Graded_{Guid.NewGuid():n}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true, Conditions = new Dictionary<string, object>(GradeIsA) },
                },
            ],
        });

        var preview = await PreviewAsync(admin, holder, type, ReadRule(type, GradeIsA));

        var shown = ItemFor(preview, gradeA);
        shown.GetProperty("visible").GetBoolean().Should().BeTrue();
        var readable = shown.GetProperty("readableFields").EnumerateArray().Select(f => f.GetString()).ToList();
        readable.Should().Contain("Title").And.NotContain("Grade");
        ItemFor(preview, gradeB).GetProperty("visible").GetBoolean().Should().BeFalse();

        var asHolder = _fixture.CreateClient();
        asHolder.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [], userId: holder.ToString()));
        var list = await asHolder.GetAsync($"/api/contents?contentType={type}&pageSize=50", Ct);
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct));
        var real = listed.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        var previewed = preview.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("visible").GetBoolean()).Select(i => i.GetProperty("id").GetGuid()).ToList();
        previewed.Should().BeEquivalentTo(real);
        real.Should().Equal(gradeA);
    }

    [Fact]
    public async Task A_key_that_differs_from_a_sensitive_field_only_by_case_is_not_readable()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive);
        var roleId = Guid.NewGuid();
        var holder = await UserAsync(roleId);
        var id = Guid.NewGuid();
        // Stored through the session, so the key keeps its own spelling.
        await StoreAsync(
            new Content
            {
                Id = id, ContentType = type, CreatedBy = holder, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["grade"] = "A" },
            },
            new Role { Id = roleId, Name = $"Cased_{Guid.NewGuid():n}", Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true } }] });

        var item = ItemFor(await PreviewAsync(admin, holder, type, ReadRule(type)), id);

        item.GetProperty("visible").GetBoolean().Should().BeFalse();
        Text(item, "reason").Should().Be("field rules hide it");
    }

    private static string[] ReadableFieldsOf(JsonElement item) =>
        item.GetProperty("readableFields").EnumerateArray().Select(f => f.GetString()!).ToArray();

    [Fact]
    public async Task A_draft_holding_view_sensitive_can_read_a_sensitive_field()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive);
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Row", user);
        var rule = ReadRule(type, readable: ["Title", "Grade"]);

        var without = ItemFor(await PreviewAsync(admin, user, type, rule), entry);
        ReadableFieldsOf(without).Should().BeEquivalentTo(["Title"]);

        var with = ItemFor(
            await PreviewAsync(admin, user, type, rule, systemCapabilities: [SystemCapabilities.ViewSensitive]), entry);
        ReadableFieldsOf(with).Should().BeEquivalentTo(["Title", "Grade"]);
    }

    [Fact]
    public async Task A_draft_named_in_a_fields_visible_to_roles_can_read_that_field()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive, gradeVisibleTo: ["Auditors"]);
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Row", user);
        var rule = ReadRule(type, readable: ["Title", "Grade"]);

        var unnamed = ItemFor(await PreviewAsync(admin, user, type, rule), entry);
        ReadableFieldsOf(unnamed).Should().BeEquivalentTo(["Title"]);

        var named = ItemFor(await PreviewAsync(admin, user, type, rule, name: "Auditors"), entry);
        ReadableFieldsOf(named).Should().BeEquivalentTo(["Title", "Grade"]);
    }

    [Fact]
    public async Task A_capability_or_name_the_save_would_refuse_is_refused_by_the_preview()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var caller = await ManageRolesCallerAsync(type);

        // A platform capability needs a platform administrator, as on the role save.
        var platform = await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type), systemCapabilities = new[] { SystemCapabilities.ManageTenants } }, Ct);
        platform.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // A reserved role name is a 400, as on the role save.
        var reserved = await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type), name = "Admin" }, Ct);
        reserved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_draft_that_edits_a_stored_role_is_listed_by_that_roles_id()
    {
        var admin = await SuperAdminAsync();
        var roleId = Guid.NewGuid();
        await StoreAsync(new Role { Id = roleId, Name = $"Listed_{Guid.NewGuid():n}", Permissions = [] });
        // A list stores role ids, not names.
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive, gradeVisibleTo: [roleId.ToString()]);
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Row", user);
        var rule = ReadRule(type, readable: ["Title", "Grade"]);

        var fresh = ItemFor(await PreviewAsync(admin, user, type, rule), entry);
        ReadableFieldsOf(fresh).Should().BeEquivalentTo(["Title"]);

        var editing = ItemFor(await PreviewAsync(admin, user, type, rule, roleId: roleId), entry);
        ReadableFieldsOf(editing).Should().BeEquivalentTo(["Title", "Grade"]);
    }

    [Fact]
    public async Task A_seeded_role_may_keep_its_reserved_name_and_a_new_role_may_not()
    {
        var admin = await SuperAdminAsync();
        var type = await TypeAsync();
        var user = await UserAsync();
        await EntryAsync(type, "Row", user);

        await PreviewAsync(admin, user, type, ReadRule(type), name: "Admin", roleId: SystemRoles.AdminRoleId);

        var refused = await admin.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type), name = "Admin" }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_role_id_that_is_unknown_deleted_or_in_another_tenant_answers_404_and_one_of_its_own_tenant_is_found()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var foreign = Guid.NewGuid();
        await StoreAsync(new Role
        {
            Id = foreign, Name = $"Foreign_{Guid.NewGuid():n}", TenantSlug = $"other{Guid.NewGuid():n}"[..14], Permissions = [],
        });
        var own = Guid.NewGuid();
        await StoreAsync(new Role { Id = own, Name = $"Own_{Guid.NewGuid():n}", TenantSlug = Tenant.DefaultSlug, Permissions = [] });
        var deleted = Guid.NewGuid();
        await StoreAsync(new Role
        {
            Id = deleted, Name = $"Gone_{Guid.NewGuid():n}", TenantSlug = Tenant.DefaultSlug, Permissions = [], DeletedAt = DateTime.UtcNow,
        });
        var caller = await ManageRolesCallerAsync(type);

        async Task<HttpStatusCode> StatusAsync(Guid id) => (await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type), roleId = id }, Ct)).StatusCode;

        // The control: a role of the caller's own tenant is found, so the 404s below come from the lookup.
        (await StatusAsync(own)).Should().Be(HttpStatusCode.OK);
        foreach (var id in new[] { foreign, deleted, Guid.NewGuid() })
            (await StatusAsync(id)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_platform_role_cannot_be_previewed_by_a_caller_who_is_not_a_platform_administrator()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var shared = Guid.NewGuid();
        await StoreAsync(new Role { Id = shared, Name = $"Shared_{Guid.NewGuid():n}", IsPlatform = true, Permissions = [] });
        var caller = await ManageRolesCallerAsync(type);

        var response = await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type), roleId = shared }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_draft_that_edits_a_stored_role_keeps_its_capabilities_unless_the_request_sets_them()
    {
        var admin = await SuperAdminAsync();
        var roleId = Guid.NewGuid();
        await StoreAsync(new Role
        {
            Id = roleId, Name = $"Sensitive_{Guid.NewGuid():n}", Permissions = [], SystemCapabilities = [SystemCapabilities.ViewSensitive],
        });
        var type = await TypeAsync(gradeSensitivity: SensitivityLevel.Sensitive);
        var user = await UserAsync();
        var entry = await EntryAsync(type, "Row", user);
        var rule = ReadRule(type, readable: ["Title", "Grade"]);

        var absent = ItemFor(await PreviewAsync(admin, user, type, rule, roleId: roleId), entry);
        ReadableFieldsOf(absent).Should().BeEquivalentTo(["Title", "Grade"]);

        var empty = ItemFor(await PreviewAsync(admin, user, type, rule, systemCapabilities: [], roleId: roleId), entry);
        ReadableFieldsOf(empty).Should().BeEquivalentTo(["Title"]);
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
    public async Task A_condition_on_a_key_the_type_does_not_declare_is_refused_even_when_the_data_holds_it()
    {
        var type = await TypeAsync();
        var user = await UserAsync();
        var id = Guid.NewGuid();
        await StoreAsync(new Content
        {
            Id = id, ContentType = type, CreatedBy = user, Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["Title"] = "Row", ["Removed"] = "secret" },
        });

        var caller = await ManageRolesCallerAsync(type);
        var conditions = new Dictionary<string, object>
        {
            ["Removed"] = new Dictionary<string, object> { ["_eq"] = "secret" },
        };
        var response = await caller.PostAsJsonAsync("/api/roles/preview",
            new { userId = user, contentType = type, permissions = ReadRule(type, conditions) }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("Removed");
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
        int rolesBefore;
        using (var scope = _fixture.Services.CreateScope())
            rolesBefore = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<Role>().CountAsync(Ct);

        var preview = await PreviewAsync(admin, user, type, ReadRule(type));
        ItemFor(preview, entry).GetProperty("visible").GetBoolean().Should().BeTrue();

        (await RealReadAsync()).Should().Be(before, "the draft must not reach the cached resolver");
        before.Should().BeFalse();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();

            // The draft is named by a fresh id, so compare the count of saved roles instead.
            (await session.Query<Role>().CountAsync(Ct)).Should().Be(rolesBefore);
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
