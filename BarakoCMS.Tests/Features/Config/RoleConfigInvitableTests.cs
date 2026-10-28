using System.Net;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>The optional <c>invitable</c> flag of the roles section: null leaves it, true or false sets it.</summary>
[Collection("Integration1")]
public class RoleConfigInvitableTests
{
    private readonly ConfigCallers _callers;

    public RoleConfigInvitableTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static object Doc(string name, object role) =>
        new { version = 1, roles = new Dictionary<string, object> { [name] = role } };

    private async Task<(string Tenant, HttpClient Client)> WriterAsync()
    {
        var tenant = await _callers.TenantAsync();
        var client = await _callers.ClientAsync(tenant,
            [await _callers.RoleAsync(SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles)]);
        return (tenant, client);
    }

    private async Task<Role> StoredAsync(string tenant, string name) =>
        (await _callers.InTenantAsync(null, s => s.Query<Role>().SingleOrDefaultAsync(r => r.Name == name && r.TenantSlug == tenant, Ct)))!;

    private async Task<Role> SeedAsync(string tenant, string name, bool invitable, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = name, TenantSlug = tenant, Description = "seeded",
            Invitable = invitable, SystemCapabilities = [.. capabilities],
        };
        await _callers.StoreInTenantAsync(null, role);
        return role;
    }

    [Fact]
    public async Task Export_writes_the_flag_and_a_plan_against_the_export_is_empty()
    {
        var (tenant, client) = await WriterAsync();
        var on = Unique("Joiner ");
        var off = Unique("Closed ");
        await SeedAsync(tenant, on, true);
        await SeedAsync(tenant, off, false);

        var (exported, export) = await ExportAsync(client, "roles");

        exported.StatusCode.Should().Be(HttpStatusCode.OK, export.ToString());
        var roles = export.GetProperty("roles");
        roles.GetProperty(on).GetProperty("invitable").GetBoolean().Should().BeTrue();
        roles.GetProperty(off).GetProperty("invitable").GetBoolean().Should().BeFalse();
        (await PlanAsync(client, new { version = 1, roles })).Body.GetProperty("isEmpty").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_document_without_the_field_never_clears_the_flag_and_plans_empty()
    {
        var (tenant, client) = await WriterAsync();
        var name = Unique("Joiner ");
        await SeedAsync(tenant, name, true);
        var document = Doc(name, new { description = "seeded" });

        (await PlanAsync(client, document)).Body.GetProperty("isEmpty").GetBoolean().Should().BeTrue();
        var (applied, body) = await ApplyAsync(client, Doc(name, new { description = "reworded" }));

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        var role = await StoredAsync(tenant, name);
        role.Description.Should().Be("reworded");
        role.Invitable.Should().BeTrue();
    }

    [Fact]
    public async Task True_and_false_set_the_flag()
    {
        var (tenant, client) = await WriterAsync();
        var name = Unique("Toggle ");
        await SeedAsync(tenant, name, false);

        var (on, onBody) = await ApplyAsync(client, Doc(name, new { description = "seeded", invitable = true }));
        on.StatusCode.Should().Be(HttpStatusCode.OK, onBody.ToString());
        (await StoredAsync(tenant, name)).Invitable.Should().BeTrue();

        var (off, offBody) = await ApplyAsync(client, Doc(name, new { description = "seeded", invitable = false }));
        off.StatusCode.Should().Be(HttpStatusCode.OK, offBody.ToString());
        (await StoredAsync(tenant, name)).Invitable.Should().BeFalse();
    }

    [Fact]
    public async Task A_new_role_can_be_created_invitable()
    {
        var (tenant, client) = await WriterAsync();
        var name = Unique("Fresh ");

        var (applied, body) = await ApplyAsync(client, Doc(name, new { description = "new", invitable = true }));

        applied.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await StoredAsync(tenant, name)).Invitable.Should().BeTrue();
    }

    [Fact]
    public async Task True_is_refused_for_a_role_that_holds_a_capability_and_nothing_changes()
    {
        var (tenant, client) = await WriterAsync();
        var held = Unique("Holder ");
        await SeedAsync(tenant, held, false, "view_audit_log");
        var created = Unique("Newbie ");

        var (onHeld, heldBody) = await ApplyAsync(client, Doc(held, new { systemCapabilities = new[] { "view_audit_log" }, invitable = true }));
        var (onNew, newBody) = await ApplyAsync(client, Doc(created, new { systemCapabilities = new[] { "view_audit_log" }, invitable = true }));

        onHeld.StatusCode.Should().Be(HttpStatusCode.BadRequest, heldBody.ToString());
        heldBody.ToString().Should().Contain("granted by an invite");
        onNew.StatusCode.Should().Be(HttpStatusCode.BadRequest, newBody.ToString());
        (await StoredAsync(tenant, held)).Invitable.Should().BeFalse();
        (await _callers.InTenantAsync(null, s => s.Query<Role>().AnyAsync(r => r.Name == created, Ct))).Should().BeFalse();
    }

    [Fact]
    public async Task A_capability_added_to_an_invitable_role_without_the_field_is_refused_like_the_roles_route()
    {
        var (tenant, client) = await WriterAsync();
        var name = Unique("Joiner ");
        await SeedAsync(tenant, name, true);

        var (applied, body) = await ApplyAsync(client, Doc(name, new { systemCapabilities = new[] { "view_audit_log" } }));

        applied.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        var role = await StoredAsync(tenant, name);
        role.Invitable.Should().BeTrue();
        role.SystemCapabilities.Should().BeEmpty();
    }
}
