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
/// caller's tenant, and removing holds the same role limits as adding.
/// </summary>
[Collection("Integration1")]
public class UserGroupMembershipTenantTests
{
    private readonly IntegrationTestFixture _factory;

    public UserGroupMembershipTenantTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> ClientAsync(string? tenant, params string[] roleNames)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync(roleNames));
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task<Guid> GroupAsync(Guid? roleId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = $"g_{Guid.NewGuid():N}",
            RoleIds = roleId is { } r ? [r] : [],
        };
        s.Store(group);
        await s.SaveChangesAsync();
        return group.Id;
    }

    /// <summary>
    /// A member of the first tenant and a user of the second, both registered. When a group is given,
    /// both are in it from both sides.
    /// </summary>
    private async Task<(string Tenant, Guid Member, Guid Outsider)> TwoTenantsAsync(Guid? groupId = null)
    {
        var tenantIn = $"club-{Guid.NewGuid():N}";
        var tenantOther = $"club-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var member = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        foreach (var id in new[] { member, outsider })
            s.Store(new User
            {
                Id = id,
                Username = $"gm-{id:N}",
                Email = $"gm-{id:N}@test.com",
                GroupIds = groupId is { } g ? [g] : [],
            });
        foreach (var slug in new[] { tenantIn, tenantOther })
            s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        s.Store(new Membership { Id = Guid.NewGuid(), UserId = member, TenantSlug = tenantIn, Status = MembershipStatus.Active });
        s.Store(new Membership { Id = Guid.NewGuid(), UserId = outsider, TenantSlug = tenantOther, Status = MembershipStatus.Active });
        if (groupId is { } gid)
        {
            var group = (await s.LoadAsync<UserGroup>(gid))!;
            group.UserIds = [member, outsider];
            s.Store(group);
        }
        await s.SaveChangesAsync();
        return (tenantIn, member, outsider);
    }

    private async Task<User> StoredUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id))!;
    }

    private async Task<UserGroup> StoredGroupAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<UserGroup>(id))!;
    }

    [Fact]
    public async Task Adding_a_user_from_another_tenant_to_a_group_answers_404_and_changes_nothing()
    {
        var groupId = await GroupAsync();
        var (tenant, _, outsider) = await TwoTenantsAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.PostAsJsonAsync($"/api/users/{outsider}/groups", new { userId = outsider, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(outsider)).GroupIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_user_in_the_callers_tenant_to_a_group_still_works()
    {
        var groupId = await GroupAsync();
        var (tenant, member, _) = await TwoTenantsAsync();
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.PostAsJsonAsync($"/api/users/{member}/groups", new { userId = member, groupId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member)).GroupIds.Should().Equal(groupId);
    }

    [Fact]
    public async Task Removing_a_user_from_another_tenant_from_a_group_answers_404_and_changes_nothing()
    {
        var groupId = await GroupAsync();
        var (tenant, member, outsider) = await TwoTenantsAsync(groupId);
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/users/{outsider}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredUserAsync(outsider)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(groupId)).UserIds.Should().BeEquivalentTo([member, outsider]);
    }

    [Fact]
    public async Task Removing_a_user_in_the_callers_tenant_from_a_group_still_works()
    {
        var groupId = await GroupAsync();
        var (tenant, member, outsider) = await TwoTenantsAsync(groupId);
        var admin = await ClientAsync(tenant, "SuperAdmin");

        var response = await admin.DeleteAsync($"/api/users/{member}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredUserAsync(member)).GroupIds.Should().BeEmpty();
        (await StoredGroupAsync(groupId)).UserIds.Should().Equal(outsider);
    }

    [Fact]
    public async Task Removing_a_user_from_a_group_holding_SuperAdmin_is_refused_to_a_caller_who_cannot_add_one()
    {
        var groupId = await GroupAsync(SystemRoles.SuperAdminRoleId);
        var (_, member, _) = await TwoTenantsAsync(groupId);
        var admin = await ClientAsync(null, "Admin");

        var response = await admin.DeleteAsync($"/api/users/{member}/groups/{groupId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredUserAsync(member)).GroupIds.Should().Equal(groupId);
        (await StoredGroupAsync(groupId)).UserIds.Should().Contain(member);
    }
}
