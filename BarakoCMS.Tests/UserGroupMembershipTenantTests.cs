using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Adding a user to a group and removing one act on a user only when that user belongs to the
/// caller's tenant, and removing holds the same role limits as adding. Groups are tenant data, so
/// each one is written to, and read back from, the tenant the request names.
/// </summary>
[Collection("Integration1")]
public class UserGroupMembershipTenantTests
{
    private readonly IntegrationTestFixture _factory;

    public UserGroupMembershipTenantTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync(string tenant, params string[] roleNames)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync(roleNames));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task<HttpClient> CallerHoldingAsync(string tenant, params string[] capabilities)
    {
        var name = $"Caller {Guid.NewGuid():n}";
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var role = new Role { Id = Guid.NewGuid(), Name = name, SystemCapabilities = [.. capabilities] };
            s.Store(role);
            s.Store(new User
            {
                Id = userId,
                Username = $"cap-{userId:n}",
                Email = $"cap-{userId:n}@example.com",
                RoleIds = [role.Id],
            });
            await s.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.CreateToken([name], userId.ToString()));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task<Guid> GroupInTenantAsync(string tenant, Guid? roleId = null, params Guid[] members)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = $"g_{Guid.NewGuid():N}",
            RoleIds = roleId is { } r ? [r] : [],
            UserIds = [.. members],
        };
        session.Store(group);
        await session.SaveChangesAsync();
        return group.Id;
    }

    private async Task<User> StoreUserAsync(string tenant, MembershipStatus status = MembershipStatus.Active, Guid? groupId = null)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"gm-{id:N}",
            Email = $"gm-{id:N}@test.com",
            GroupIds = groupId is { } g ? [g] : [],
        };
        session.Store(user);
        session.Store(new Membership { Id = Guid.NewGuid(), UserId = id, TenantSlug = tenant, Status = status });
        await session.SaveChangesAsync();
        return user;
    }

    private async Task<string> RegisteredTenantAsync()
    {
        var slug = $"club-{Guid.NewGuid():N}";
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>
    /// A tenant with a group in it, a member of that tenant who is in the group, and a user whose
    /// only membership is in another tenant who is also in the group from both sides.
    /// </summary>
    private async Task<(string Tenant, Guid GroupId, User Member, User Outsider)> SeededAsync(Guid? roleId = null)
    {
        var tenant = await RegisteredTenantAsync();
        var other = await RegisteredTenantAsync();
        var groupId = await GroupInTenantAsync(tenant, roleId);
        var member = await StoreUserAsync(tenant, groupId: groupId);
        var outsider = await StoreUserAsync(other, groupId: groupId);
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var group = (await session.LoadAsync<UserGroup>(groupId))!;
        group.UserIds = [member.Id, outsider.Id];
        session.Store(group);
        await session.SaveChangesAsync();
        return (tenant, groupId, member, outsider);
    }

    private async Task<User> StoredUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id))!;
    }

    private async Task<UserGroup> StoredGroupAsync(string tenant, Guid id)
    {
        await using var session = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        return (await session.LoadAsync<UserGroup>(id))!;
    }

    private async Task<int> AuditRowsAsync(string action, Guid targetId)
    {
        using var scope = _factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == action && e.TargetId == targetId.ToString())
            .ToListAsync();
        return rows.Count;
    }

    [Fact]
    public async Task Adding_a_user_from_another_tenant_to_a_group_answers_404_and_changes_nothing()
    {
        var tenant = await RegisteredTenantAsync();
        var groupId = await GroupInTenantAsync(tenant);
        var outsider = await StoreUserAsync(await RegisteredTenantAsync());
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.PostAsJsonAsync($"/api/users/{outsider.Id}/groups", new { userId = outsider.Id, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(outsider.Id)).GroupIds.Should().BeEmpty();
        (await AuditRowsAsync("user.group.assigned", outsider.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Adding_a_user_in_the_callers_tenant_to_a_group_still_works()
    {
        var tenant = await RegisteredTenantAsync();
        var groupId = await GroupInTenantAsync(tenant);
        var member = await StoreUserAsync(tenant);
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.PostAsJsonAsync($"/api/users/{member.Id}/groups", new { userId = member.Id, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member.Id)).GroupIds.Should().Equal(groupId);
        (await AuditRowsAsync("user.group.assigned", member.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Adding_a_user_whose_membership_is_suspended_to_a_group_answers_404_and_changes_nothing()
    {
        var tenant = await RegisteredTenantAsync();
        var groupId = await GroupInTenantAsync(tenant);
        var suspended = await StoreUserAsync(tenant, MembershipStatus.Suspended);
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.PostAsJsonAsync($"/api/users/{suspended.Id}/groups", new { userId = suspended.Id, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(suspended.Id)).GroupIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_user_from_another_tenant_from_a_group_answers_404_and_changes_nothing()
    {
        var (tenant, groupId, member, outsider) = await SeededAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/users/{outsider.Id}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(outsider.Id)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().BeEquivalentTo([member.Id, outsider.Id]);
        (await AuditRowsAsync("user.group.removed", outsider.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Removing_a_user_in_the_callers_tenant_from_a_group_still_works()
    {
        var (tenant, groupId, member, outsider) = await SeededAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/users/{member.Id}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member.Id)).GroupIds.Should().BeEmpty();
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().Equal(outsider.Id);
        (await AuditRowsAsync("user.group.removed", member.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Removing_a_user_from_a_group_holding_SuperAdmin_is_refused_to_a_caller_who_cannot_add_one()
    {
        var (tenant, groupId, member, outsider) = await SeededAsync(SystemRoles.SuperAdminRoleId);
        var admin = await ClientAsync(tenant, "Admin");

        var response = await admin.DeleteAsync($"/api/users/{member.Id}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUserAsync(member.Id)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().BeEquivalentTo([member.Id, outsider.Id]);
    }

    [Fact]
    public async Task Removing_a_user_from_the_group_side_who_belongs_to_another_tenant_answers_404_and_changes_nothing()
    {
        var (tenant, groupId, member, outsider) = await SeededAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/user-groups/{groupId}/users/{outsider.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(outsider.Id)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().BeEquivalentTo([member.Id, outsider.Id]);
    }

    [Fact]
    public async Task Removing_a_user_in_the_callers_tenant_from_the_group_side_still_works()
    {
        var (tenant, groupId, member, outsider) = await SeededAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/user-groups/{groupId}/users/{member.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member.Id)).GroupIds.Should().BeEmpty();
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().Equal(outsider.Id);
    }

    [Fact]
    public async Task Removing_a_user_from_the_group_side_of_a_role_bearing_group_is_refused_to_a_caller_who_cannot_grant_roles()
    {
        var tenant = await RegisteredTenantAsync();
        var groupId = await GroupInTenantAsync(tenant, SystemRoles.SuperAdminRoleId);
        var member = await StoreUserAsync(tenant, groupId: groupId);
        await using (var session = _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant))
        {
            var group = (await session.LoadAsync<UserGroup>(groupId))!;
            group.UserIds = [member.Id];
            session.Store(group);
            await session.SaveChangesAsync();
        }
        var groupsOnly = await CallerHoldingAsync(tenant, SystemCapabilities.ManageUserGroups);

        var response = await groupsOnly.DeleteAsync($"/api/user-groups/{groupId}/users/{member.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUserAsync(member.Id)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(tenant, groupId)).UserIds.Should().Equal(member.Id);
    }
}
