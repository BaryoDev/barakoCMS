using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// Who holds which role, globally and inside a tenant. These changes were already recorded; what the
/// rows lacked was what the person held before, and one path (creating a tenant) wrote a membership
/// with no row at all.
/// </summary>
[Collection("Sequential")]
public class MembershipGrantAuditTests
{
    private readonly IntegrationTestFixture _factory;

    public MembershipGrantAuditTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<Guid> UserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User { Id = id, Username = $"grant-{id:N}", Email = $"grant-{id:N}@example.com" });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<string> RoleNameAsync(Guid roleId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<Role>(roleId))!.Name;
    }

    private async Task<List<AuditEvent>> TenantRowsAsync(string slug, string action)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var rows = await session.Query<AuditEvent>()
            .Where(e => e.TenantSlug == slug && e.Action == action)
            .ToListAsync();
        return rows.ToList();
    }

    [Fact]
    public async Task Creating_a_tenant_records_its_creator_as_the_first_member_in_that_tenants_log()
    {
        var client = await SuperAdminAsync();
        var handle = $"aud-{Guid.NewGuid():N}"[..16];

        var created = await client.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        var rows = await TenantRowsAsync(handle, "tenant.member.added");

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TargetType.Should().Be("User");
        row.ActorUserId.Should().NotBeNull();
        row.TargetId.Should().Be(row.ActorUserId.ToString(), "the creator is the member being added");
        row.Strings("roleIds").Should().Equal(SystemRoles.AdminRoleId.ToString());
        row.Strings("roleNames").Should().Equal(await RoleNameAsync(SystemRoles.AdminRoleId));
    }

    /// <summary>
    /// A tenant switched off issues no tokens and refuses its keys, which takes access from every
    /// member at once without touching a membership.
    /// </summary>
    [Fact]
    public async Task Switching_a_tenant_off_and_on_writes_a_row_each_way_and_an_unrelated_edit_writes_none()
    {
        var client = await SuperAdminAsync();
        var handle = $"aud-{Guid.NewGuid():N}"[..16];
        var created = await client.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true });
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());

        async Task PutAsync(string name, bool isActive)
        {
            var response = await client.PutAsJsonAsync($"/api/tenants/{handle}", new { name, isActive });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        await PutAsync("renamed, still on", true);
        (await TenantRowsAsync(handle, "tenant.member.added")).Should().HaveCount(1, "this tenant's rows are reachable");
        (await TenantRowsAsync(handle, "tenant.deactivated")).Should().BeEmpty();
        (await TenantRowsAsync(handle, "tenant.activated")).Should().BeEmpty();

        await PutAsync("off", false);
        var off = await TenantRowsAsync(handle, "tenant.deactivated");
        off.Should().HaveCount(1);
        off[0].TargetType.Should().Be("Tenant");
        off[0].TargetId.Should().Be(handle);
        off[0].ActorUserId.Should().NotBeNull();

        await PutAsync("on", true);
        (await TenantRowsAsync(handle, "tenant.activated")).Should().HaveCount(1);
        (await TenantRowsAsync(handle, "tenant.deactivated")).Should().HaveCount(1);
    }

    /// <summary>
    /// Adding somebody who is already a member replaces their roles and lifts a suspension. The row
    /// said "added" with nothing held before, which reads as a first grant.
    /// </summary>
    [Theory]
    [InlineData(MembershipStatus.Active)]
    [InlineData(MembershipStatus.Suspended)]
    public async Task Adding_somebody_who_is_already_a_member_records_what_they_held_before(MembershipStatus status)
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, _) = await AuditTenants.AdminAsync(_factory, slug);
        var memberId = await AuditTenants.MemberAsync(_factory, slug, status, SystemRoles.HRRoleId);

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email = $"aud-{memberId:N}@example.com", roleIds = new[] { SystemRoles.UserRoleId } });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "tenant.member.added", memberId.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Text("previousStatus").Should().Be(status.ToString());
        row.Strings("previousRoleIds").Should().Equal(SystemRoles.HRRoleId.ToString());
        row.Strings("previousRoleNames").Should().Equal(await RoleNameAsync(SystemRoles.HRRoleId));
        row.Strings("roleIds").Should().Equal(SystemRoles.UserRoleId.ToString());
        row.Strings("roleNames").Should().Equal(await RoleNameAsync(SystemRoles.UserRoleId));
    }

    [Fact]
    public async Task Adding_a_new_member_names_the_roles_and_records_nothing_held_before()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, _) = await AuditTenants.AdminAsync(_factory, slug);
        var email = $"new-{Guid.NewGuid():N}@example.com";

        var added = await client.PostAsJsonAsync("/api/tenants/members",
            new { email, roleIds = new[] { SystemRoles.UserRoleId } });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());

        var rows = await TenantRowsAsync(slug, "tenant.member.added");

        rows.Should().HaveCount(1);
        rows[0].Strings("roleNames").Should().Equal(await RoleNameAsync(SystemRoles.UserRoleId));
        rows[0].Metadata.Should().NotContainKey("previousStatus");
        rows[0].Metadata.Should().NotContainKey("previousRoleIds");
    }

    [Fact]
    public async Task Changing_a_members_roles_records_what_they_held_before()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, adminId) = await AuditTenants.AdminAsync(_factory, slug);
        var memberId = await AuditTenants.MemberAsync(_factory, slug, MembershipStatus.Active, SystemRoles.HRRoleId);

        var updated = await client.PutAsJsonAsync($"/api/tenants/members/{memberId}",
            new { roleIds = new[] { SystemRoles.UserRoleId }, status = "Suspended" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "tenant.member.updated", memberId.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.TenantSlug.Should().Be(slug);
        row.ActorUserId.Should().Be(adminId);
        row.Text("previousStatus").Should().Be("Active");
        row.Strings("previousRoleIds").Should().Equal(SystemRoles.HRRoleId.ToString());
        row.Strings("previousRoleNames").Should().Equal(await RoleNameAsync(SystemRoles.HRRoleId));
        row.Text("status").Should().Be("Suspended");
        row.Strings("roleIds").Should().Equal(SystemRoles.UserRoleId.ToString());
        row.Strings("roleNames").Should().Equal(await RoleNameAsync(SystemRoles.UserRoleId));
    }

    [Fact]
    public async Task Removing_a_member_records_the_roles_they_lose()
    {
        var slug = await AuditTenants.CreateAsync(_factory);
        var (client, _) = await AuditTenants.AdminAsync(_factory, slug);
        var memberId = await AuditTenants.MemberAsync(_factory, slug, MembershipStatus.Suspended, SystemRoles.UserRoleId);

        var removed = await client.DeleteAsync($"/api/tenants/members/{memberId}");
        removed.StatusCode.Should().Be(HttpStatusCode.OK, await removed.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "tenant.member.removed", memberId.ToString());

        rows.Should().HaveCount(1);
        rows[0].Text("previousStatus").Should().Be("Suspended");
        rows[0].Strings("previousRoleIds").Should().Equal(SystemRoles.UserRoleId.ToString());
        rows[0].Strings("previousRoleNames").Should().Equal(await RoleNameAsync(SystemRoles.UserRoleId));
    }

    [Fact]
    public async Task Assigning_a_global_role_records_its_name()
    {
        var client = await SuperAdminAsync();
        var userId = await UserAsync();

        var assigned = await client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId = SystemRoles.UserRoleId });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "user.role.assigned", userId.ToString());

        rows.Should().HaveCount(1);
        rows[0].Text("roleId").Should().Be(SystemRoles.UserRoleId.ToString());
        rows[0].Text("roleName").Should().Be(await RoleNameAsync(SystemRoles.UserRoleId));
    }

    [Fact]
    public async Task Removing_a_role_the_user_never_held_writes_no_row()
    {
        var client = await SuperAdminAsync();
        var userId = await UserAsync();

        var assigned = await client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId = SystemRoles.UserRoleId });
        assigned.StatusCode.Should().Be(HttpStatusCode.OK, await assigned.Content.ReadAsStringAsync());

        var never = await client.DeleteAsync($"/api/users/{userId}/roles/{SystemRoles.HRRoleId}");
        never.StatusCode.Should().Be(HttpStatusCode.OK, await never.Content.ReadAsStringAsync());

        (await AuditRows.ForTargetAsync(_factory, "user.role.assigned", userId.ToString()))
            .Should().HaveCount(1, "the user's rows are reachable, so an empty result below means none was written");
        (await AuditRows.ForTargetAsync(_factory, "user.role.removed", userId.ToString())).Should().BeEmpty();

        var held = await client.DeleteAsync($"/api/users/{userId}/roles/{SystemRoles.UserRoleId}");
        held.StatusCode.Should().Be(HttpStatusCode.OK, await held.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "user.role.removed", userId.ToString());
        rows.Should().HaveCount(1);
        rows[0].Text("roleName").Should().Be(await RoleNameAsync(SystemRoles.UserRoleId));
    }
}
