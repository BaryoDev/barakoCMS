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
/// <c>/api/users/{id}/roles</c> writes <see cref="User.RoleIds"/>, the roles a user holds in every
/// tenant. Only a caller whose own capability comes from one of those platform-wide roles may change
/// them. An administrator whose roles come from a tenant membership manages that tenant's roster
/// through <c>/api/tenants/members</c> instead.
/// </summary>
[Collection("Sequential")]
public class PlatformRoleGrantTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ipCounter;

    public PlatformRoleGrantTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static string NextIp() =>
        $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private async Task<Guid> UserAsync(params Guid[] globalRoleIds)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"grant-{id:N}",
            Email = $"grant-{id:N}@example.com",
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
        var claims = new Dictionary<string, string> { ["Username"] = $"grant-{userId:N}" };
        if (tenant is not null) claims["tenant"] = tenant;

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles, userId.ToString(), claims));
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    /// <summary>A user who is Admin of one tenant through a membership and holds no global role.</summary>
    private async Task<(Guid UserId, HttpClient Client)> TenantAdminAsync()
    {
        var slug = $"grants-{Guid.NewGuid():N}"[..16];
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

        return (userId, ClientFor(userId, ["Admin"], slug));
    }

    [Fact]
    public async Task A_tenant_admin_cannot_grant_itself_a_global_role()
    {
        var (adminId, client) = await TenantAdminAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/users/{adminId}/roles", new { roleId = SystemRoles.AdminRoleId });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "Admin of one tenant must not become Admin of every tenant");
        (await LoadAsync(adminId)).RoleIds.Should().NotContain(SystemRoles.AdminRoleId);
    }

    [Fact]
    public async Task A_tenant_admin_cannot_grant_a_global_role_to_someone_else()
    {
        var (_, client) = await TenantAdminAsync();
        var target = await UserAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/users/{target}/roles", new { roleId = SystemRoles.AdminRoleId });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoadAsync(target)).RoleIds.Should().NotContain(SystemRoles.AdminRoleId);
    }

    [Fact]
    public async Task A_tenant_admin_cannot_remove_a_global_role()
    {
        var (_, client) = await TenantAdminAsync();
        var target = await UserAsync(SystemRoles.AdminRoleId);

        var res = await client.DeleteAsync($"/api/users/{target}/roles/{SystemRoles.AdminRoleId}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoadAsync(target)).RoleIds.Should().Contain(SystemRoles.AdminRoleId);
    }

    [Fact]
    public async Task A_global_admin_can_still_grant_and_remove_an_ordinary_role()
    {
        var adminId = await UserAsync(SystemRoles.AdminRoleId);
        var client = ClientFor(adminId, ["Admin"]);
        var target = await UserAsync();

        var granted = await client.PostAsJsonAsync(
            $"/api/users/{target}/roles", new { roleId = SystemRoles.UserRoleId });
        granted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(target)).RoleIds.Should().Contain(SystemRoles.UserRoleId);

        var removed = await client.DeleteAsync($"/api/users/{target}/roles/{SystemRoles.UserRoleId}");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(target)).RoleIds.Should().NotContain(SystemRoles.UserRoleId);
    }

    [Fact]
    public async Task An_admin_cannot_remove_SuperAdmin()
    {
        var adminId = await UserAsync(SystemRoles.AdminRoleId);
        var client = ClientFor(adminId, ["Admin"]);
        var target = await UserAsync(SystemRoles.SuperAdminRoleId);

        var res = await client.DeleteAsync($"/api/users/{target}/roles/{SystemRoles.SuperAdminRoleId}");

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "removing SuperAdmin is a SuperAdmin act, the same as granting it");
        (await LoadAsync(target)).RoleIds.Should().Contain(SystemRoles.SuperAdminRoleId);
    }

    [Fact]
    public async Task A_SuperAdmin_can_remove_SuperAdmin_while_another_remains()
    {
        var callerId = await UserAsync(SystemRoles.SuperAdminRoleId);
        var client = ClientFor(callerId, ["SuperAdmin"]);
        var target = await UserAsync(SystemRoles.SuperAdminRoleId);

        var res = await client.DeleteAsync($"/api/users/{target}/roles/{SystemRoles.SuperAdminRoleId}");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(target)).RoleIds.Should().NotContain(SystemRoles.SuperAdminRoleId);
    }

    [Fact]
    public async Task The_last_SuperAdmin_keeps_the_role()
    {
        var lastId = await UserAsync(SystemRoles.SuperAdminRoleId);
        var client = ClientFor(lastId, ["SuperAdmin"]);

        // The shared database may hold other SuperAdmins (other tests' callers). Take the role off
        // them for this test and put it back afterwards, so "last" is really last.
        List<Guid> others;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var holders = await session.Query<User>()
                .Where(u => u.RoleIds.Contains(SystemRoles.SuperAdminRoleId) && u.Id != lastId)
                .ToListAsync();
            others = holders.Select(u => u.Id).ToList();
            foreach (var holder in holders)
            {
                holder.RoleIds.Remove(SystemRoles.SuperAdminRoleId);
                session.Store(holder);
            }
            await session.SaveChangesAsync();
        }

        try
        {
            var res = await client.DeleteAsync($"/api/users/{lastId}/roles/{SystemRoles.SuperAdminRoleId}");

            res.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "removing the last SuperAdmin leaves nobody able to manage roles or tenants");
            (await res.Content.ReadAsStringAsync()).Should().Contain("last SuperAdmin");
            (await LoadAsync(lastId)).RoleIds.Should().Contain(SystemRoles.SuperAdminRoleId);
        }
        finally
        {
            using var scope = _fixture.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var id in others)
            {
                var user = await session.LoadAsync<User>(id);
                if (user is not null && !user.RoleIds.Contains(SystemRoles.SuperAdminRoleId))
                {
                    user.RoleIds.Add(SystemRoles.SuperAdminRoleId);
                    session.Store(user);
                }
            }
            await session.SaveChangesAsync();
        }
    }
}
