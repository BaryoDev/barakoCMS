using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Tests.Features.Config;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Xunit;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// A global role grants in every tenant an account enters, so it is a platform role, and a tenant's
/// administrators change nothing about it (#913). role-tenancy.sql makes a role held globally a
/// platform role (RoleTenancyMigrationTests); these pin what holds once it has.
/// </summary>
[Collection("Integration1")]
public class GlobalRoleOwnershipTests(IntegrationTestFixture fixture)
{
    private readonly ConfigCallers _callers = new(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private Task<Role?> LoadAsync(Guid id) => _callers.InTenantAsync(null, s => s.LoadAsync<Role>(id, Ct));

    [Fact]
    public async Task A_tenant_admin_with_manage_roles_from_a_membership_changes_no_role_held_globally()
    {
        var tenant = await _callers.TenantAsync();
        var held = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("Held Globally "), Description = "before", IsPlatform = true,
            SystemCapabilities = [SystemCapabilities.ViewAuditLog],
        };
        await _callers.StoreInTenantAsync(null, held);
        var grants = await _callers.TenantRoleAsync(tenant, SystemCapabilities.ManageConfig, SystemCapabilities.ManageRoles);
        var admin = await _callers.ClientAsync(tenant, [], memberRoles: [grants]);

        var own = Unique("Own ");
        var (control, controlBody) = await ApplyAsync(admin, new { version = 1, roles = new Dictionary<string, object> { [own] = new { description = "x" } } });
        control.StatusCode.Should().Be(HttpStatusCode.OK, "the control: the same caller writes a role of its own tenant. " + controlBody);

        var (applied, body) = await ApplyAsync(admin, new
        {
            version = 1,
            roles = new Dictionary<string, object>
            {
                [held.Name] = new { description = "after", systemCapabilities = new[] { SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewJobs } },
            },
        });
        applied.StatusCode.Should().Be(HttpStatusCode.Forbidden, body.ToString());
        body.ToString().Should().Contain("platform role every tenant shares");

        var put = await admin.PutAsJsonAsync($"/api/roles/{held.Id}", new
        {
            name = held.Name, description = "after", permissions = Array.Empty<object>(),
            systemCapabilities = new[] { SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewJobs },
        }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden, await put.Content.ReadAsStringAsync(Ct));

        var stored = await LoadAsync(held.Id);
        stored!.Description.Should().Be("before");
        stored.SystemCapabilities.Should().Equal(SystemCapabilities.ViewAuditLog);
    }

    [Fact]
    public async Task A_tenants_role_held_globally_grants_nothing_in_another_tenant()
    {
        var owner = await _callers.TenantAsync();
        var other = await _callers.TenantAsync();
        var role = await _callers.TenantRoleAsync(owner, SystemCapabilities.ViewAuditLog);

        var inOwner = await _callers.ClientAsync(owner, [role], memberRoles: []);
        (await inOwner.GetAsync("/api/audit", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the control: it grants in its own tenant");

        var inOther = await _callers.ClientAsync(other, [role], memberRoles: []);
        (await inOther.GetAsync("/api/audit", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role another tenant owns grants nothing here, however it is held");
    }

    [Fact]
    public async Task Assigning_a_global_role_takes_a_platform_role_and_refuses_a_tenants_role()
    {
        var tenant = await _callers.TenantAsync();
        var tenantRole = new Role { Id = Guid.NewGuid(), Name = Unique("Shop Desk "), TenantSlug = tenant };
        var platformRole = new Role { Id = Guid.NewGuid(), Name = Unique("Everywhere "), IsPlatform = true };
        await _callers.StoreInTenantAsync(null, tenantRole, platformRole);
        var target = new User { Id = Guid.NewGuid(), Username = Unique("target-"), Email = $"{Guid.NewGuid():n}@example.com", RoleIds = [] };
        await _callers.StoreInTenantAsync(null, target);
        var client = await _callers.SuperAdminAsync(null);

        var platform = await client.PostAsJsonAsync($"/api/users/{target.Id}/roles", new { roleId = platformRole.Id }, Ct);
        platform.StatusCode.Should().Be(HttpStatusCode.OK, "the control: " + await platform.Content.ReadAsStringAsync(Ct));

        var refused = await client.PostAsJsonAsync($"/api/users/{target.Id}/roles", new { roleId = tenantRole.Id }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(tenantRole.Name).And.Contain(tenant);

        var stored = await _callers.InTenantAsync(null, s => s.LoadAsync<User>(target.Id, Ct));
        stored!.RoleIds.Should().Equal(platformRole.Id);
    }
}
