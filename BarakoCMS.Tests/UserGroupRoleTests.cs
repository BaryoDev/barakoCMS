using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A user group holds roles, and its members hold them too (#919). Groups used to be stored,
/// listed and assigned, and changed nothing about what anyone could do.
/// </summary>
[Collection("Sequential")]
public class UserGroupRoleTests
{
    private readonly IntegrationTestFixture _factory;

    public UserGroupRoleTests(IntegrationTestFixture factory) => _factory = factory;

    private static string Slug() => $"grp{Guid.NewGuid():n}";

    private async Task<(Guid RoleId, string Slug)> ReaderRoleAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = Slug();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Reader_{Guid.NewGuid():n}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } }],
        };
        session.Store(role);
        await session.SaveChangesAsync();
        return (role.Id, slug);
    }

    private async Task<Guid> UserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new User { Id = Guid.NewGuid(), Username = $"member_{Guid.NewGuid():n}", Email = $"m_{Guid.NewGuid():n}@example.com" };
        session.Store(user);
        await session.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> GroupAsync(params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"g_{Guid.NewGuid():n}", RoleIds = roleIds.ToList() };
        session.Store(group);
        await session.SaveChangesAsync();
        return group.Id;
    }

    private async Task<bool> CanReadAsync(Guid userId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = (await session.LoadAsync<User>(userId))!;
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        return await resolver.CanPerformActionAsync(user, slug, "read");
    }

    private async Task<HttpClient> ClientAsync(params string[] roleNames)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync(roleNames));
        return client;
    }

    [Fact]
    public async Task A_member_of_a_group_holding_a_role_gets_that_roles_permissions()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();
        var outsider = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        (await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await CanReadAsync(member, slug)).Should().BeTrue();
        (await CanReadAsync(outsider, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task A_member_listed_on_the_group_side_gets_the_roles_too()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        (await admin.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await CanReadAsync(member, slug)).Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_member_from_the_group_takes_the_role_back()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });
        await admin.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member });
        (await CanReadAsync(member, slug)).Should().BeTrue();

        (await admin.DeleteAsync($"/api/users/{member}/groups/{groupId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CanReadAsync(member, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task A_group_with_no_roles_changes_nobodys_access()
    {
        var (_, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync();
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });

        (await CanReadAsync(member, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task A_SuperAdmin_sets_a_groups_roles_and_the_list_returns_them()
    {
        var (roleId, _) = await ReaderRoleAsync();
        var groupId = await GroupAsync();

        var admin = await ClientAsync("SuperAdmin");
        var put = await admin.PutAsJsonAsync($"/api/user-groups/{groupId}",
            new { id = groupId, name = "renamed", description = "d", roleIds = new[] { roleId } });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var get = await admin.GetStringAsync($"/api/user-groups/{groupId}");
        get.Should().Contain(roleId.ToString());
    }

    [Fact]
    public async Task An_update_that_names_no_roles_leaves_them_alone()
    {
        var (roleId, _) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);

        var admin = await ClientAsync("SuperAdmin");
        (await admin.PutAsJsonAsync($"/api/user-groups/{groupId}", new { id = groupId, name = "renamed", description = "d" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.LoadAsync<UserGroup>(groupId))!.RoleIds.Should().Equal(roleId);
    }

    [Fact]
    public async Task An_Admin_cannot_put_the_SuperAdmin_role_on_a_group()
    {
        var groupId = await GroupAsync();
        Guid superAdminRole;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            superAdminRole = SystemRoles.SuperAdminRoleId;
            (await session.LoadAsync<Role>(superAdminRole)).Should().NotBeNull();
        }

        var admin = await ClientAsync("Admin");
        var put = await admin.PutAsJsonAsync($"/api/user-groups/{groupId}",
            new { id = groupId, name = "g", description = "d", roleIds = new[] { superAdminRole } });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var check = _factory.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<UserGroup>(groupId);
        stored!.RoleIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_without_group_capability_cannot_set_roles()
    {
        var (roleId, _) = await ReaderRoleAsync();
        var groupId = await GroupAsync();

        var outsider = await ClientAsync("NoSuchRole");
        var put = await outsider.PutAsJsonAsync($"/api/user-groups/{groupId}",
            new { id = groupId, name = "g", description = "d", roleIds = new[] { roleId } });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_tenant_owned_role_is_refused_on_a_group()
    {
        Guid tenantRole;
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = new Role { Id = Guid.NewGuid(), Name = $"Tenant_{Guid.NewGuid():n}", TenantSlug = "some-tenant" };
            session.Store(role);
            await session.SaveChangesAsync();
            tenantRole = role.Id;
        }

        var groupId = await GroupAsync();
        var admin = await ClientAsync("SuperAdmin");
        var put = await admin.PutAsJsonAsync($"/api/user-groups/{groupId}",
            new { id = groupId, name = "g", description = "d", roleIds = new[] { tenantRole } });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_role_that_does_not_exist_is_refused_on_a_group()
    {
        var groupId = await GroupAsync();
        var admin = await ClientAsync("SuperAdmin");
        var put = await admin.PutAsJsonAsync($"/api/user-groups/{groupId}",
            new { id = groupId, name = "g", description = "d", roleIds = new[] { Guid.NewGuid() } });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
