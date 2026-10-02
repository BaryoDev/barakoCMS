using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
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

    private static async Task<Guid> CreateAsync(HttpClient client, string name, params string[] capabilities)
    {
        var response = await client.PostAsJsonAsync("/api/roles", new
        {
            name,
            description = "for the audit tests",
            permissions = Array.Empty<object>(),
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

        var response = await client.PostAsJsonAsync("/api/roles", new
        {
            name,
            description = "for the audit tests",
            permissions = new[]
            {
                new
                {
                    contentTypeSlug = "article",
                    create = new { enabled = true },
                    read = new { enabled = true },
                    update = new
                    {
                        enabled = true,
                        conditions = new Dictionary<string, object> { ["department"] = new { _eq = literal } },
                    },
                    delete = new { enabled = false },
                },
            },
            systemCapabilities = new[] { SystemCapabilities.ViewAuditLog },
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>())!.Id;

        var rows = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TargetType.Should().Be("Role");
        row.TenantSlug.Should().Be(Tenant.DefaultSlug);
        row.ActorUserId.Should().NotBeNull();
        row.ActorUserId.Should().NotBe(Guid.Empty);
        row.ActorUsername.Should().StartWith("stored-");
        row.Text("name").Should().Be(name);
        row.Strings("capabilities").Should().Equal(SystemCapabilities.ViewAuditLog);
        row.Strings("permissions").Should().Equal("article: create, read, update (conditional)");
        row.Json().Should().NotContain(literal, "a rule's conditions hold values, and a row holds names");
    }

    [Fact]
    public async Task Changing_a_roles_capabilities_writes_the_before_and_after_lists()
    {
        var client = await SuperAdminAsync();
        var name = RoleName();
        var id = await CreateAsync(client, name, SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewMonitoring);

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
        row.Strings("capabilitiesBefore").Should()
            .Equal(SystemCapabilities.ViewAuditLog, SystemCapabilities.ViewMonitoring);
        row.Strings("capabilitiesAfter").Should()
            .Equal(SystemCapabilities.ViewMonitoring, SystemCapabilities.ManageApiKeys);
        row.Strings("capabilitiesAdded").Should().Equal(SystemCapabilities.ManageApiKeys);
        row.Strings("capabilitiesRemoved").Should().Equal(SystemCapabilities.ViewAuditLog);
        row.Text("nameBefore").Should().Be(name);
    }

    /// <summary>
    /// Role names are uniquely indexed, so renaming one role to another's name makes the save fail.
    /// The row is staged on the same session as the role, so the failed save takes both with it.
    /// </summary>
    [Fact]
    public async Task A_role_update_whose_save_fails_leaves_neither_the_change_nor_a_row()
    {
        var client = await SuperAdminAsync();
        var taken = RoleName();
        var name = RoleName();
        await CreateAsync(client, taken);
        var id = await CreateAsync(client, name, SystemCapabilities.ViewAuditLog);

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
        var id = await CreateAsync(client, RoleName(), SystemCapabilities.ViewAuditLog);

        var deleted = await client.DeleteAsync($"/api/roles/{id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "role.deleted", id.ToString());

        rows.Should().HaveCount(1);
        rows[0].Strings("capabilities").Should().Equal(SystemCapabilities.ViewAuditLog);
    }

    /// <summary>
    /// Roles are global documents, so nothing about the role says which tenant's log the row belongs
    /// in. It goes to the tenant the request resolved to.
    /// </summary>
    [Fact]
    public async Task A_SuperAdmin_acting_in_a_tenant_writes_the_row_to_that_tenants_log()
    {
        var slug = $"aud-{Guid.NewGuid():N}"[..16];
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            await session.SaveChangesAsync();
        }

        var id = await CreateAsync(await SuperAdminAsync(slug), RoleName(), SystemCapabilities.ViewAuditLog);

        var rows = await AuditRows.ForTargetAsync(_factory, "role.created", id.ToString());

        rows.Should().HaveCount(1);
        rows[0].TenantSlug.Should().Be(slug);
    }
}
