using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// A role is what gives somebody a capability, so creating one and changing one are grant changes.
/// Only deleting one used to be recorded, and that row held the name alone.
/// </summary>
[Collection("Sequential")]
public class RoleGrantAuditTests
{
    private readonly IntegrationTestFixture _factory;

    public RoleGrantAuditTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> SuperAdminAsync(string? tenant = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        if (tenant is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static string RoleName() => $"Audit role {Guid.NewGuid():N}";

    private static object ArticlePermission(string field, object condition) => new
    {
        contentTypeSlug = "article",
        create = new { enabled = true },
        read = new { enabled = true },
        update = new
        {
            enabled = true,
            conditions = new Dictionary<string, object> { [field] = condition },
        },
        delete = new { enabled = false },
    };

    private static async Task<Guid> CreateAsync(
        HttpClient client, string name, string[] capabilities, params object[] permissions)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new
        {
            name,
            description = "for the audit tests",
            permissions,
            systemCapabilities = capabilities,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>())!.Id;
    }

    private async Task<Role?> StoredAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<Role>(id);
    }

    [Fact]
    public async Task Creating_a_role_writes_a_row_naming_its_capabilities_and_permissions()
    {
        var client = await SuperAdminAsync();
        var name = RoleName();
        var literal = $"condition-{Guid.NewGuid():N}";

        var id = await CreateAsync(client, name, [SystemCapabilities.ViewAuditLog],
            ArticlePermission("department", new { _eq = literal }));

        var rows = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TargetType.Should().Be("Role");
        row.TenantSlug.Should().Be(Tenant.DefaultSlug);
        row.ActorUserId.Should().NotBeNull();
        row.ActorUserId.Should().NotBe(Guid.Empty);
        row.ActorUsername.Should().StartWith("stored-");
        row.Text("name").Should().Be(name);
        row.Items("capabilities").Should().Equal(SystemCapabilities.ViewAuditLog);

        var permissions = row.Element("permissions");
        permissions.GetProperty("count").GetInt32().Should().Be(1);
        var permission = permissions.GetProperty("items")[0];
        permission.GetProperty("contentType").GetString().Should().Be("article");
        permission.GetProperty("actions").EnumerateArray().Select(a => a.GetString())
            .Should().Equal("create", "read", "update");
        var condition = permission.GetProperty("conditions").GetProperty("items")[0];
        condition.GetProperty("rule").GetString().Should().Be("update");
        condition.GetProperty("field").GetString().Should().Be("department");
        condition.GetProperty("operators").EnumerateArray().Select(o => o.GetString()).Should().Equal("_eq");

        row.Json().Should().NotContain(literal, "a condition holds a value, and a row holds names");
    }

    [Fact]
    public async Task Changing_a_roles_capabilities_writes_the_before_and_after_lists()
    {
        var client = await SuperAdminAsync();
        var name = RoleName();
        var id = await CreateAsync(client, name, [SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewMonitoring]);

        var update = await client.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "changed",
            permissions = Array.Empty<object>(),
            systemCapabilities = new[] { SystemCapabilities.ViewMonitoring, SystemCapabilities.ManageApiKeys },
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "role.updated", id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.ActorUserId.Should().NotBeNull();
        row.ActorUserId.Should().NotBe(Guid.Empty);
        row.Items("capabilitiesBefore").Should()
            .Equal(SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewMonitoring);
        row.Items("capabilitiesAfter").Should()
            .Equal(SystemCapabilities.ViewMonitoring, SystemCapabilities.ManageApiKeys);
        row.Items("capabilitiesAdded").Should().Equal(SystemCapabilities.ManageApiKeys);
        row.Items("capabilitiesRemoved").Should().Equal(SystemCapabilities.ViewAuditLog);
        row.Text("nameBefore").Should().Be(name);
        row.Element("conditionsChanged").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// The edit that widens access without touching a capability or an action: the rule tests the
    /// same field with the same operator and accepts one more value.
    /// </summary>
    [Fact]
    public async Task A_condition_only_edit_shows_a_change_and_holds_no_value()
    {
        var client = await SuperAdminAsync();
        var name = RoleName();
        var first = $"first-{Guid.NewGuid():N}";
        var second = $"second-{Guid.NewGuid():N}";
        var id = await CreateAsync(client, name, [], ArticlePermission("department", new { _in = new[] { first } }));

        var update = await client.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name,
            description = "for the audit tests",
            permissions = new[] { ArticlePermission("department", new { _in = new[] { first, second } }) },
            systemCapabilities = Array.Empty<string>(),
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "role.updated", id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Element("conditionsChanged").GetBoolean().Should().BeTrue();
        row.Items("conditionsChangedIn").Should().Equal("article");
        row.Element("permissionsBefore").GetRawText().Should().Be(
            row.Element("permissionsAfter").GetRawText(), "the field and the operator are the same");
        row.Metadata.Should().NotContainKey("capabilitiesAdded");
        row.Json().Should().NotContain(first);
        row.Json().Should().NotContain(second);
    }

    /// <summary>
    /// Role names are uniquely indexed, so renaming one role to another's name makes the save fail.
    /// </summary>
    /// <remarks>
    /// What this proves is one direction only: a row is not committed ahead of the change it
    /// describes. It would go red if the row were saved first, on its own. It would not notice the
    /// row being moved to a second save after the role's, since the first save throws before that
    /// one is reached. That the two share one save is read from the endpoint, which calls
    /// <c>SaveChangesAsync</c> once.
    /// </remarks>
    [Fact]
    public async Task A_role_update_whose_save_fails_leaves_neither_the_change_nor_a_row()
    {
        var client = await SuperAdminAsync();
        var taken = RoleName();
        var name = RoleName();
        await CreateAsync(client, taken, []);
        var id = await CreateAsync(client, name, [SystemCapabilities.ViewAuditLog]);

        var update = await client.PutAsJsonAsync($"/api/roles/{id}", new
        {
            name = taken,
            description = "this save cannot commit",
            permissions = Array.Empty<object>(),
            systemCapabilities = new[] { SystemCapabilities.ManageApiKeys },
        });

        update.IsSuccessStatusCode.Should().BeFalse("two roles cannot hold one name");

        var stored = await StoredAsync(id);
        stored!.Name.Should().Be(name);
        stored.SystemCapabilities.Should().Equal(SystemCapabilities.ViewAuditLog);

        var created = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());
        created.Should().HaveCount(1, "the role's own create row is there, so the query reaches this role's rows");
        (await AuditRows.ForTargetAsync(_factory, "role.updated", id.ToString())).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_role_records_the_capabilities_it_held()
    {
        var client = await SuperAdminAsync();
        var id = await CreateAsync(client, RoleName(), [SystemCapabilities.ViewAuditLog]);

        var deleted = await client.DeleteAsync($"/api/roles/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "role.deleted", id.ToString());

        rows.Should().HaveCount(1);
        rows[0].Items("capabilities").Should().Equal(SystemCapabilities.ViewAuditLog);
    }

    /// <summary>
    /// Roles are global documents, so nothing about the role says which tenant's log the row belongs
    /// in. It goes to the tenant the request resolved to.
    /// </summary>
    [Fact]
    public async Task A_SuperAdmin_acting_in_a_tenant_writes_the_row_to_that_tenants_log()
    {
        var slug = await AuditTenants.CreateAsync(_factory);

        var id = await CreateAsync(await SuperAdminAsync(slug), RoleName(), [SystemCapabilities.ViewAuditLog]);

        var rows = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());

        rows.Should().HaveCount(1);
        rows[0].TenantSlug.Should().Be(slug);
    }

    /// <summary>
    /// A role row lands in the log of whichever tenant the platform administrator was resolved to,
    /// and that tenant's administrators read the log without being able to list roles.
    /// </summary>
    [Fact]
    public async Task A_caller_who_cannot_list_roles_is_shown_the_role_row_without_its_lists()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var name = RoleName();
        var id = await CreateAsync(await SuperAdminAsync(slug), name, [SystemCapabilities.ManageTenants],
            ArticlePermission("department", new { _eq = "x" }));

        var viewer = await AuditTenants.HolderOfAsync(_factory, slug, SystemCapabilities.ViewAuditLog);
        var listed = await AuditRows.ListedAsync(viewer, "role.created", id.ToString());

        listed.Should().HaveCount(1);
        listed[0].Action.Should().Be("role.created");
        listed[0].ActorUserId.Should().NotBeNull();
        listed[0].TargetType.Should().Be("Role");
        listed[0].Metadata.Should().NotBeNull();
        listed[0].Metadata!.Keys.Should().Equal("name");
        listed[0].Metadata!["name"].ToString().Should().Be(name);
        JsonSerializer.Serialize(listed[0]).Should().NotContain(SystemCapabilities.ManageTenants);
        JsonSerializer.Serialize(listed[0]).Should().NotContain("article");

        var stored = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());
        stored.Should().HaveCount(1);
        stored[0].Items("capabilities").Should().Equal(new[] { SystemCapabilities.ManageTenants }, "the stored row is complete");
    }

    [Fact]
    public async Task A_caller_who_can_list_roles_is_shown_the_role_row_in_full()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var id = await CreateAsync(await SuperAdminAsync(slug), RoleName(), [SystemCapabilities.ManageTenants]);

        var lister = await AuditTenants.HolderOfAsync(
            _factory, slug, SystemCapabilities.ViewAuditLog, SystemCapabilities.ManageRoles);
        var listed = await AuditRows.ListedAsync(lister, "role.created", id.ToString());

        listed.Should().HaveCount(1);
        listed[0].Metadata.Should().NotBeNull();
        listed[0].Metadata!.Keys.Should().Contain(new[] { "name", "capabilities", "permissions" });
        JsonSerializer.Serialize(listed[0].Metadata!["capabilities"]).Should().Contain(SystemCapabilities.ManageTenants);
    }
}
