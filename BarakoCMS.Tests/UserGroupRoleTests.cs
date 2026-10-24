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
[Collection("Integration2")]
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

    private async Task<HttpClient> CallerHoldingAsync(params string[] capabilities)
    {
        var name = $"Caller {Guid.NewGuid():n}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role { Id = Guid.NewGuid(), Name = name, SystemCapabilities = capabilities.ToList() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"cap-{userId:n}",
            Email = $"cap-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken([name], userId.ToString()));
        return client;
    }

    private async Task<UserGroup> StoredGroupAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<UserGroup>(id))!;
    }

    private async Task<User> StoredUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id))!;
    }

    [Fact]
    public async Task A_caller_who_cannot_grant_roles_cannot_add_a_user_to_a_role_bearing_group_from_the_group_side()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var caller = await CallerHoldingAsync(SystemCapabilities.ManageUserGroups);
        var post = await caller.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member });

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredGroupAsync(groupId)).UserIds.Should().BeEmpty();
        (await CanReadAsync(member, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task A_caller_who_cannot_grant_roles_cannot_put_a_user_in_a_group_holding_SuperAdmin_from_the_user_side()
    {
        var groupId = await GroupAsync(SystemRoles.SuperAdminRoleId);
        var member = await UserAsync();

        var admin = await ClientAsync("Admin");
        var post = await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUserAsync(member)).GroupIds.Should().BeEmpty();
        (await StoredGroupAsync(groupId)).UserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_caller_allowed_to_grant_roles_can_assign_a_role_bearing_group_from_the_user_side()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var caller = await CallerHoldingAsync(SystemCapabilities.ManageUserMembership);
        var post = await caller.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });

        post.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member)).GroupIds.Should().Equal(groupId);
        (await CanReadAsync(member, slug)).Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_allowed_to_grant_roles_can_add_a_member_from_the_group_side()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var caller = await CallerHoldingAsync(SystemCapabilities.ManageUserGroups, SystemCapabilities.ManageUserMembership);
        (await caller.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredGroupAsync(groupId)).UserIds.Should().Equal(member);
        (await CanReadAsync(member, slug)).Should().BeTrue();
    }

    [Fact]
    public async Task Adding_a_member_to_a_group_with_no_roles_needs_no_role_rights()
    {
        var groupId = await GroupAsync();
        var viaGroup = await UserAsync();
        var viaUser = await UserAsync();

        var groupsOnly = await CallerHoldingAsync(SystemCapabilities.ManageUserGroups);
        (await groupsOnly.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = viaGroup }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await ClientAsync("Admin");
        (await admin.PostAsJsonAsync($"/api/users/{viaUser}/groups", new { userId = viaUser, groupId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredGroupAsync(groupId)).UserIds.Should().Contain(viaGroup);
        (await StoredUserAsync(viaUser)).GroupIds.Should().Equal(groupId);
    }

    [Fact]
    public async Task Removing_from_the_group_side_takes_back_a_role_added_from_the_user_side()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });
        (await CanReadAsync(member, slug)).Should().BeTrue();

        (await admin.DeleteAsync($"/api/user-groups/{groupId}/users/{member}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredUserAsync(member)).GroupIds.Should().NotContain(groupId);
        (await CanReadAsync(member, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task Removing_from_the_user_side_takes_back_a_role_added_from_the_group_side()
    {
        var (roleId, slug) = await ReaderRoleAsync();
        var groupId = await GroupAsync(roleId);
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        await admin.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member });
        (await CanReadAsync(member, slug)).Should().BeTrue();

        (await admin.DeleteAsync($"/api/users/{member}/groups/{groupId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredGroupAsync(groupId)).UserIds.Should().NotContain(member);
        (await CanReadAsync(member, slug)).Should().BeFalse();
    }

    [Fact]
    public async Task A_group_whose_only_member_is_listed_on_the_group_side_cannot_be_deleted()
    {
        var groupId = await GroupAsync();
        var member = await UserAsync();

        var admin = await ClientAsync("SuperAdmin");
        await admin.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { groupId, userId = member });
        (await StoredGroupAsync(groupId)).UserIds.Should().Equal(member);

        (await admin.DeleteAsync($"/api/user-groups/{groupId}")).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StoredGroupAsync(groupId)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_caller_who_cannot_grant_roles_cannot_create_a_group_holding_a_role()
    {
        var (roleId, _) = await ReaderRoleAsync();

        var caller = await CallerHoldingAsync(SystemCapabilities.ManageUserGroups);
        var post = await caller.PostAsJsonAsync("/api/user-groups",
            new { name = $"g_{Guid.NewGuid():n}", description = "d", userIds = Array.Empty<Guid>(), roleIds = new[] { roleId } });

        post.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
