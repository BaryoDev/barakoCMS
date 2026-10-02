using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Users;

/// <summary>
/// A role carrying <c>view_hidden</c> is handed out only by a SuperAdmin, on both surfaces that
/// assign roles. A role carrying <c>view_sensitive</c> is assigned as any role is.
/// </summary>
/// <remarks>
/// Before the capability existed only SuperAdmin read a Hidden value, and no role an Admin could
/// assign opened one. With it, a role a SuperAdmin creates for an auditor would otherwise be a role
/// an Admin, or the administrator of one tenant, gives to itself. <c>view_sensitive</c> is the
/// access the HR role carried, and an Admin could always assign HR, so it stays assignable.
///
/// The callers are built the way <see cref="PlatformRoleGrantTests"/> builds them.
/// </remarks>
[Collection("Sequential")]
public class HiddenCapabilityGrantTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ipCounter;

    public HiddenCapabilityGrantTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static string NextIp() =>
        $"198.51.101.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private async Task<Guid> UserAsync(params Guid[] globalRoleIds)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"hidden-{id:N}",
            Email = $"hidden-{id:N}@example.com",
            RoleIds = globalRoleIds.ToList(),
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<User> LoadAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.LoadAsync<User>(id))!;
    }

    private HttpClient ClientFor(Guid userId, string[] roles, string? tenant = null)
    {
        var claims = new Dictionary<string, string> { ["Username"] = $"hidden-{userId:N}" };
        if (tenant is not null) claims["tenant"] = tenant;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles, userId.ToString(), claims));
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    /// <summary>A user who is Admin of one tenant through a membership and holds no global role.</summary>
    private async Task<(Guid UserId, HttpClient Client, string Slug)> TenantAdminAsync()
    {
        var slug = $"hidden-{Guid.NewGuid():N}"[..16];
        var userId = await UserAsync();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.AdminRoleId],
            });
            await session.SaveChangesAsync();
        }

        return (userId, ClientFor(userId, ["Admin"], slug), slug);
    }

    private async Task<Guid> RoleHoldingAsync(string capability)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new Role
        {
            Id = id,
            Name = $"{capability}-{id:N}",
            SystemCapabilities = [capability],
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<bool> AnyMembershipHoldsAsync(Guid roleId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Membership>().AnyAsync(m => m.RoleIds.Contains(roleId));
    }

    [Fact]
    public async Task A_global_admin_cannot_grant_itself_a_role_holding_view_hidden()
    {
        var adminId = await UserAsync(SystemRoles.AdminRoleId);
        var client = ClientFor(adminId, ["Admin"]);
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewHidden);

        var res = await client.PostAsJsonAsync($"/api/users/{adminId}/roles", new { roleId });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "only SuperAdmin read a Hidden value before, and an Admin must not hand itself the role that does now");
        (await res.Content.ReadAsStringAsync()).Should().Contain("platform administrator");
        (await LoadAsync(adminId)).RoleIds.Should().NotContain(roleId);
    }

    [Fact]
    public async Task A_tenant_admin_cannot_give_a_member_a_role_holding_view_hidden()
    {
        var (_, client, _) = await TenantAdminAsync();
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewHidden);
        var email = $"member-{Guid.NewGuid():N}@example.com";

        var res = await client.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { roleId } });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync()).Should().Contain("platform administrator");
        (await AnyMembershipHoldsAsync(roleId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_tenant_admin_cannot_update_itself_to_a_role_holding_view_hidden()
    {
        var (adminId, client, _) = await TenantAdminAsync();
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewHidden);

        var res = await client.PutAsJsonAsync($"/api/tenants/members/{adminId}",
            new { roleIds = new[] { SystemRoles.AdminRoleId, roleId }, status = "Active" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AnyMembershipHoldsAsync(roleId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_SuperAdmin_can_grant_a_role_holding_view_hidden()
    {
        var callerId = await UserAsync(SystemRoles.SuperAdminRoleId);
        var client = ClientFor(callerId, ["SuperAdmin"]);
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewHidden);
        var target = await UserAsync();

        var res = await client.PostAsJsonAsync($"/api/users/{target}/roles", new { roleId });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await LoadAsync(target)).RoleIds.Should().Contain(roleId);
    }

    [Fact]
    public async Task A_global_admin_can_grant_a_role_holding_view_sensitive()
    {
        var adminId = await UserAsync(SystemRoles.AdminRoleId);
        var client = ClientFor(adminId, ["Admin"]);
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewSensitive);
        var target = await UserAsync();

        var res = await client.PostAsJsonAsync($"/api/users/{target}/roles", new { roleId });

        res.StatusCode.Should().Be(HttpStatusCode.OK,
            "an Admin could always assign HR, which is what carried this access");
        (await LoadAsync(target)).RoleIds.Should().Contain(roleId);
    }

    [Fact]
    public async Task A_tenant_admin_can_give_a_member_a_role_holding_view_sensitive()
    {
        var (_, client, _) = await TenantAdminAsync();
        var roleId = await RoleHoldingAsync(SystemCapabilities.ViewSensitive);
        var email = $"member-{Guid.NewGuid():N}@example.com";

        var res = await client.PostAsJsonAsync("/api/tenants/members", new { email, roleIds = new[] { roleId } });

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        (await AnyMembershipHoldsAsync(roleId)).Should().BeTrue();
    }
}
