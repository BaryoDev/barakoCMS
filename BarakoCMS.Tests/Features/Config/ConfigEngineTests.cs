using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// Export, plan and apply over the content type and role sections: a round trip, a plan that writes
/// nothing, the destructive flag, the gates and tenant isolation (#921).
/// </summary>
[Collection("Integration3")]
public class ConfigEngineTests
{
    private readonly ConfigCallers _callers;

    public ConfigEngineTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    [Fact]
    public async Task An_exported_type_applied_to_an_empty_tenant_is_reproduced_and_a_second_plan_is_empty()
    {
        var source = await _callers.TenantAsync();
        var target = await _callers.TenantAsync();
        var type = Type(Unique("article"), ("Title", "string"), ("Body", "text"));
        type.IsPubliclyDeliverable = true;
        type.RouteTemplate = "/articles/{slug}";
        await _callers.StoreInTenantAsync(source, type);

        var (exported, document) = await ExportAsync(await _callers.SuperAdminAsync(source), "contentTypes");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, document.ToString());

        var targetClient = await _callers.SuperAdminAsync(target);
        var (applied, plan) = await ApplyAsync(targetClient, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Equal($"contentTypes/{type.Name} create");

        var copy = await _callers.InTenantAsync(target, s => s.Query<ContentTypeDefinition>().SingleOrDefaultAsync(t => t.Name == type.Name, Ct));
        copy.Should().NotBeNull("the apply created the type in the target tenant");
        copy!.Fields.Select(f => (f.Name, f.Type)).Should().Equal(("Title", "string"), ("Body", "text"));
        copy.IsPubliclyDeliverable.Should().BeTrue();
        copy.RouteTemplate.Should().Be("/articles/{slug}");

        var (_, second) = await PlanAsync(targetClient, document);
        second.GetProperty("isEmpty").GetBoolean().Should().BeTrue(second.ToString());
    }

    [Fact]
    public async Task A_plan_names_what_it_would_create_and_writes_nothing()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("event");
        var role = Unique("Planner ");
        var document = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["contentTypes"] = new Dictionary<string, object> { [name] = Type(name, ("Title", "string")) },
            ["roles"] = new Dictionary<string, object> { [role] = new { description = "plans", systemCapabilities = new[] { "view_audit_log" } } },
        };

        var (response, plan) = await PlanAsync(await _callers.SuperAdminAsync(tenant), document);

        response.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().BeEquivalentTo($"contentTypes/{name} create", $"roles/{role} create");
        plan.GetProperty("applied").GetBoolean().Should().BeFalse();
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == name, Ct)))
            .Should().BeFalse("a plan runs the apply in a transaction it rolls back");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().AnyAsync(r => r.Name == role, Ct)))
            .Should().BeFalse("nor does it store a role");
    }

    [Fact]
    public async Task A_removed_field_is_a_destructive_change_that_apply_refuses_without_the_flag()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("ticket");
        await _callers.StoreInTenantAsync(tenant, Type(name, ("Title", "string"), ("Notes", "text")));
        var client = await _callers.SuperAdminAsync(tenant);
        var document = new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = Type(name, ("Title", "string")) },
        };

        var (_, plan) = await PlanAsync(client, document);
        Changes(plan).Should().Equal($"contentTypes/{name} destructive");
        plan.GetProperty("hasDestructiveChanges").GetBoolean().Should().BeTrue();
        plan.ToString().Should().Contain("field 'Notes' is removed");

        var (refused, refusedBody) = await ApplyAsync(client, document);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, refusedBody.ToString());
        refusedBody.GetProperty("applied").GetBoolean().Should().BeFalse();
        (await FieldsAsync(tenant, name)).Should().Equal("Title", "Notes");

        var (allowed, allowedBody) = await ApplyAsync(client, document, allowDestructive: true);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, allowedBody.ToString());
        (await FieldsAsync(tenant, name)).Should().Equal("Title");
    }

    private async Task<List<string>> FieldsAsync(string tenant, string name) =>
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!
        .Fields.Select(f => f.Name).ToList();

    [Fact]
    public async Task A_caller_without_manage_config_is_refused_on_all_three_routes()
    {
        var tenant = await _callers.TenantAsync();
        var document = new { version = 1, contentTypes = new Dictionary<string, object>() };

        var admin = await _callers.SuperAdminAsync(tenant);
        (await ExportAsync(admin)).Response.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");
        (await PlanAsync(admin, document)).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var schemaOnly = await _callers.ClientAsync(tenant, [await _callers.RoleAsync(SystemCapabilities.ManageContentTypes)]);
        (await ExportAsync(schemaOnly)).Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PlanAsync(schemaOnly, document)).Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApplyAsync(schemaOnly, document)).Response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Each_section_also_takes_its_own_capability()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("gated");
        var document = new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = Type(name, ("Title", "string")) },
        };

        var both = await _callers.ClientAsync(tenant,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageContentTypes)]);
        (await PlanAsync(both, document)).Response.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control");

        var configOnly = await _callers.ClientAsync(tenant, [await _callers.RoleAsync(SystemCapabilities.ManageConfig)]);
        var (planned, plan) = await PlanAsync(configOnly, document);
        planned.StatusCode.Should().Be(HttpStatusCode.Forbidden, plan.ToString());
        plan.ToString().Should().Contain("manage_content_types");

        var (_, export) = await ExportAsync(configOnly);
        export.GetProperty("withheld").EnumerateArray().Select(e => e.GetString()).Should().Contain("contentTypes");
        export.TryGetProperty("contentTypes", out _).Should().BeFalse("a section the caller may not read is withheld, not exported");
    }

    [Fact]
    public async Task Turning_public_delivery_on_for_a_stored_type_takes_manage_public_delivery()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("private");
        await _callers.StoreInTenantAsync(tenant, Type(name, ("Title", "string")));
        var exposed = Type(name, ("Title", "string"));
        exposed.IsPubliclyDeliverable = true;
        var document = new { version = 1, contentTypes = new Dictionary<string, object> { [name] = exposed } };

        var schemaOnly = await _callers.ClientAsync(tenant,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageContentTypes)]);
        var (refused, body) = await ApplyAsync(schemaOnly, document);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain(SystemCapabilities.ManagePublicDelivery);
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!
            .IsPubliclyDeliverable.Should().BeFalse("deciding what an anonymous caller reads is not the schema grant's");

        var discloser = await _callers.ClientAsync(tenant,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageContentTypes, SystemCapabilities.ManagePublicDelivery)]);
        var (allowed, allowedBody) = await ApplyAsync(discloser, document);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, allowedBody.ToString());
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!
            .IsPubliclyDeliverable.Should().BeTrue();
    }

    [Fact]
    public async Task Export_reads_and_apply_writes_only_the_callers_tenant()
    {
        var mine = await _callers.TenantAsync();
        var theirs = await _callers.TenantAsync();
        var myType = Unique("mine");
        var theirType = Unique("theirs");
        await _callers.StoreInTenantAsync(mine, Type(myType, ("Title", "string")));
        await _callers.StoreInTenantAsync(theirs, Type(theirType, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(mine);

        var (_, export) = await ExportAsync(client, "contentTypes");
        var exported = export.GetProperty("contentTypes").EnumerateObject().Select(p => p.Name).ToList();
        exported.Should().NotBeEmpty();
        exported.Should().Contain(myType).And.NotContain(theirType);

        var created = Unique("created");
        var (applied, plan) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [myType] = Type(myType, ("Title", "string")), [created] = Type(created, ("Title", "string")) },
        });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        (await _callers.InTenantAsync(mine, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == created, Ct))).Should().BeTrue();
        (await _callers.InTenantAsync(theirs, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == created, Ct)))
            .Should().BeFalse("an apply writes the caller's tenant and no other");
    }

    [Fact]
    public async Task A_role_is_created_and_a_second_plan_against_it_is_empty()
    {
        var name = Unique("Shop Clerk ");
        var document = new
        {
            version = 1,
            roles = new Dictionary<string, object> { [name] = new { description = "Sees the ward", systemCapabilities = new[] { "view_audit_log" }, requireMfa = true } },
        };
        var client = await _callers.SuperAdminAsync(null);

        var (applied, plan) = await ApplyAsync(client, document);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        var role = await _callers.InTenantAsync(null, s => s.Query<Role>().SingleOrDefaultAsync(r => r.Name == name, Ct));
        role.Should().NotBeNull();
        role!.SystemCapabilities.Should().Equal("view_audit_log");
        role.RequireMfa.Should().BeTrue();
        (await PlanAsync(client, document)).Body.GetProperty("isEmpty").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_role_written_with_manage_roles_from_a_membership_belongs_to_that_tenant()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("Tenant Made ");
        var document = new { version = 1, roles = new Dictionary<string, object> { [name] = new { description = "x" } } };
        var grants = await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles);

        var memberOnly = await _callers.ClientAsync(tenant, [], memberRoles: [grants]);
        var (applied, body) = await ApplyAsync(memberOnly, document);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var role = await _callers.InTenantAsync(null, s => s.Query<Role>().SingleOrDefaultAsync(r => r.Name == name, Ct));
        role.Should().NotBeNull();
        role!.TenantSlug.Should().Be(tenant, "a role made in a tenant is that tenant's, the rule POST /api/roles follows");
        role.IsPlatform.Should().BeFalse();
    }

    [Fact]
    public async Task A_role_can_be_applied_under_the_name_of_a_deleted_one_in_the_same_tenant()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("Was Deleted ");
        var deleted = new Role { Id = Guid.NewGuid(), Name = name, TenantSlug = tenant, DeletedAt = DateTime.UtcNow };
        await _callers.StoreInTenantAsync(null, deleted);
        var document = new { version = 1, roles = new Dictionary<string, object> { [name] = new { description = "fresh" } } };

        var (applied, body) = await ApplyAsync(await _callers.SuperAdminAsync(tenant), document);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var named = await _callers.InTenantAsync(null, s => s.Query<Role>().Where(r => r.Name == name).ToListAsync(Ct));
        named.Should().HaveCount(1, "the deleted holder of the name was erased with the create");
        named![0].Id.Should().NotBe(deleted.Id);
        named[0].DeletedAt.Should().BeNull();
        named[0].Description.Should().Be("fresh");
    }

    [Fact]
    public async Task A_tenant_export_and_apply_reach_neither_another_tenants_role_nor_list_it()
    {
        var mine = await _callers.TenantAsync();
        var other = await _callers.TenantAsync();
        var shared = Unique("Front Desk ");
        var own = Unique("Own Desk ");
        var theirs = new Role { Id = Guid.NewGuid(), Name = shared, TenantSlug = other, Description = "theirs", SystemCapabilities = ["view_audit_log"] };
        await _callers.StoreInTenantAsync(null, theirs,
            new Role { Id = Guid.NewGuid(), Name = own, TenantSlug = mine, Description = "mine" });
        var client = await _callers.ClientAsync(mine,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles)]);

        var (exported, export) = await ExportAsync(client, "roles");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, export.ToString());
        var keys = export.GetProperty("roles").EnumerateObject().Select(p => p.Name).ToList();
        keys.Should().Contain(own, "the control: the tenant's own role is exported");
        keys.Should().NotContain(shared, "another tenant's role is not this tenant's to export");

        var document = new
        {
            version = 1,
            roles = new Dictionary<string, object> { [shared] = new { description = "changed by mine", systemCapabilities = new[] { "view_jobs" } } },
        };
        var (applied, body) = await ApplyAsync(client, document);

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        Changes(body).Should().Equal($"roles/{shared} create");
        var stored = await _callers.InTenantAsync(null, s => s.Query<Role>().Where(r => r.Name == shared).ToListAsync(Ct));
        stored.Should().HaveCount(2, "the name makes a role of this tenant beside the other tenant's");
        var untouched = stored.Single(r => r.Id == theirs.Id);
        untouched.Description.Should().Be("theirs");
        untouched.SystemCapabilities.Should().Equal("view_audit_log");
        stored.Single(r => r.Id != theirs.Id).TenantSlug.Should().Be(mine);
    }

    [Fact]
    public async Task A_role_carrying_a_platform_capability_is_written_only_by_a_platform_role_administrator()
    {
        var name = Unique("Escalator ");
        var document = new { version = 1, roles = new Dictionary<string, object> { [name] = new { systemCapabilities = new[] { "manage_users" } } } };

        var roleWriter = await _callers.ClientAsync(null,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles)]);

        var plain = Unique("Plain ");
        var (control, controlBody) = await ApplyAsync(roleWriter,
            new { version = 1, roles = new Dictionary<string, object> { [plain] = new { systemCapabilities = new[] { "view_audit_log" } } } });
        control.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: the same caller writes a role with no platform capability. " + controlBody);

        var (refused, body) = await ApplyAsync(roleWriter, document);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("reaches past the tenant", "the refusal is the platform capability rule, not another gate");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().AnyAsync(r => r.Name == name, Ct))).Should().BeFalse();

        var (allowed, allowedBody) = await ApplyAsync(await _callers.SuperAdminAsync(null), document);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, allowedBody.ToString());
    }

    [Fact]
    public async Task An_unknown_section_or_a_newer_version_is_refused_and_named()
    {
        var client = await _callers.SuperAdminAsync(await _callers.TenantAsync());

        var (unknown, unknownBody) = await PlanAsync(client, new { version = 1, portal = new { } });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        unknownBody.ToString().Should().Contain("'portal' is not a section");

        var (newer, newerBody) = await PlanAsync(client, new { version = 2 });
        newer.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        newerBody.ToString().Should().Contain("version 2");

        var (tenants, tenantsBody) = await ApplyAsync(client, new { version = 1, tenants = new { } });
        tenants.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tenantsBody.ToString().Should().Contain("Seed:File");
    }

    /// <summary>A role holding a rule on a type of a fresh tenant, and the document that changes the rule.</summary>
    private async Task<(string Tenant, string Role, object Document)> RuleChangeAsync(bool platform = false)
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("ledger");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var role = Unique("Ruled ");
        await _callers.StoreInTenantAsync(null, new Role { Id = Guid.NewGuid(), Name = role, TenantSlug = platform ? null : tenant, IsPlatform = platform });
        var document = new
        {
            version = 1,
            permissionRules = new Dictionary<string, object> { [$"{role}/{type}"] = new { read = new { enabled = true } } },
        };
        return (tenant, role, document);
    }

    [Fact]
    public async Task Rules_on_a_tenants_role_take_manage_roles_from_a_membership_and_reach_no_other_tenants_role()
    {
        var (tenant, role, document) = await RuleChangeAsync();
        var grants = await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles);

        var memberOnly = await _callers.ClientAsync(tenant, [], memberRoles: [grants]);
        var (applied, body) = await ApplyAsync(memberOnly, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct)))!.Permissions.Should().ContainSingle();

        var (_, otherRole, _) = await RuleChangeAsync();
        var mineType = Unique("ledger");
        await _callers.StoreInTenantAsync(tenant, Type(mineType, ("Title", "string")));
        var (refused, plan) = await ApplyAsync(memberOnly, new
        {
            version = 1,
            permissionRules = new Dictionary<string, object> { [$"{otherRole}/{mineType}"] = new { read = new { enabled = true } } },
        });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, plan.ToString());
        plan.ToString().Should().Contain($"there is no role '{otherRole}'", "another tenant's role is out of reach");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == otherRole, Ct)))!.Permissions.Should().BeEmpty();
    }

    /// <summary>A caller of <paramref name="tenant"/> holding manage_config and manage_roles through a membership only.</summary>
    private async Task<HttpClient> MemberRoleWriterAsync(string tenant) =>
        await _callers.ClientAsync(tenant, [], memberRoles:
            [await _callers.TenantRoleAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles)]);

    [Fact]
    public async Task A_role_writer_from_a_membership_is_refused_a_platform_role_and_it_is_unchanged()
    {
        var tenant = await _callers.TenantAsync();
        var platform = new Role { Id = Guid.NewGuid(), Name = Unique("Shared Desk "), Description = "before", IsPlatform = true };
        await _callers.StoreInTenantAsync(null, platform);

        var (applied, body) = await ApplyAsync(await MemberRoleWriterAsync(tenant),
            new { version = 1, roles = new Dictionary<string, object> { [platform.Name] = new { description = "after" } } });

        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("platform role every tenant shares");
        (await _callers.InTenantAsync(null, s => s.LoadAsync<Role>(platform.Id, Ct)))!.Description.Should().Be("before");
    }

    [Fact]
    public async Task A_role_writer_from_a_membership_is_refused_a_new_role_carrying_manage_roles_and_none_is_made()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("Widened ");

        var (applied, body) = await ApplyAsync(await MemberRoleWriterAsync(tenant), new
        {
            version = 1,
            roles = new Dictionary<string, object> { [name] = new { systemCapabilities = new[] { SystemCapabilities.ManageRoles } } },
        });

        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("reaches past the tenant");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().AnyAsync(r => r.Name == name, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_role_writer_from_a_membership_is_refused_a_platform_roles_rules_and_they_are_unchanged()
    {
        var (tenant, role, document) = await RuleChangeAsync(platform: true);

        var (applied, body) = await ApplyAsync(await MemberRoleWriterAsync(tenant), document);

        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("platform role every tenant shares");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct)))!.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_platform_callers_change_to_a_platform_role_says_it_changes_every_tenant()
    {
        var tenant = await _callers.TenantAsync();
        var platform = new Role { Id = Guid.NewGuid(), Name = Unique("Shared Desk "), Description = "before", IsPlatform = true };
        var own = new Role { Id = Guid.NewGuid(), Name = Unique("Own Desk "), Description = "before", TenantSlug = tenant };
        await _callers.StoreInTenantAsync(null, platform, own);

        var (_, plan) = await PlanAsync(await _callers.SuperAdminAsync(tenant), new
        {
            version = 1,
            roles = new Dictionary<string, object> { [platform.Name] = new { description = "after" }, [own.Name] = new { description = "after" } },
        });

        var notes = plan.GetProperty("sections")[0].GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        notes.Should().ContainSingle("only the platform role is shared. " + plan);
        notes[0].Should().Contain($"'{platform.Name}' is a platform role every tenant shares");
    }

    [Fact]
    public async Task A_rule_on_a_platform_role_is_written_only_by_a_platform_role_administrator()
    {
        var (tenant, role, document) = await RuleChangeAsync(platform: true);
        var writer = await _callers.ClientAsync(tenant, [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles)]);

        var (refused, body) = await ApplyAsync(writer, document);
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("platform role every tenant shares");
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct)))!.Permissions.Should().BeEmpty();

        var (allowed, allowedBody) = await ApplyAsync(await _callers.SuperAdminAsync(tenant), document);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, allowedBody.ToString());
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleAsync(r => r.Name == role, Ct)))!.Permissions.Should().ContainSingle();
    }

    [Fact]
    public async Task Taking_a_capability_away_from_a_role_is_destructive()
    {
        var name = Unique("Shrinking ");
        await _callers.StoreInTenantAsync(null, new Role { Id = Guid.NewGuid(), Name = name, SystemCapabilities = ["view_audit_log", "view_jobs"] });
        var client = await _callers.SuperAdminAsync(null);

        var (_, grows) = await PlanAsync(client, new { version = 1, roles = new Dictionary<string, object> { [name] = new { systemCapabilities = new[] { "view_audit_log", "view_jobs", "view_modules" } } } });
        Changes(grows).Should().Equal(new[] { $"roles/{name} change" }, "the control: granting more takes nothing away");

        var (_, shrinks) = await PlanAsync(client, new { version = 1, roles = new Dictionary<string, object> { [name] = new { systemCapabilities = new[] { "view_audit_log" } } } });
        Changes(shrinks).Should().Equal($"roles/{name} destructive");
    }

    [Fact]
    public async Task An_unknown_section_named_on_export_is_refused_by_the_validator()
    {
        var client = await _callers.SuperAdminAsync(await _callers.TenantAsync());

        (await ExportAsync(client, "contentTypes")).Response.StatusCode.Should().Be(HttpStatusCode.OK, "the control");
        var (refused, body) = await ExportAsync(client, "contentTypes,portal");

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.ToString().Should().Contain("'portal' is not a section");
    }

    [Fact]
    public void Section_names_that_clash_or_take_a_reserved_key_stop_the_host()
    {
        var act = () => barakoCMS.Features.Config.ConfigSectionCheck.Check(["contentTypes", "roles"]);
        act.Should().NotThrow("the control: distinct, unreserved names");

        ((Action)(() => barakoCMS.Features.Config.ConfigSectionCheck.Check(["roles", "roles"]))).Should().Throw<InvalidOperationException>()
            .WithMessage("*'roles' is registered by 2*");
        foreach (var reserved in new[] { "version", "gaps", "withheld", "exportedAt", "contentsWithheld", "tenants" })
        {
            ((Action)(() => barakoCMS.Features.Config.ConfigSectionCheck.Check([reserved]))).Should().Throw<InvalidOperationException>()
                .WithMessage($"*'{reserved}' is a reserved key*");
        }
    }
}
