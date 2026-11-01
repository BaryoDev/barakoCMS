using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Adding a user to a group names a user that exists in the caller's tenant, and removing a user from a
/// group needs a live group.
/// </summary>
[Collection("Integration1")]
public class GroupMemberIdTests
{
    private readonly IntegrationTestFixture _factory;

    public GroupMemberIdTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adding_a_user_id_that_does_not_exist_answers_404_and_leaves_the_group_alone()
    {
        var groupId = await StoreGroupAsync();
        var client = await ClientAsync();

        var response = await client.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { userId = Guid.NewGuid() }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadGroupAsync(groupId)).UserIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Adding_a_user_from_another_tenant_answers_404_and_a_member_of_the_callers_tenant_is_added()
    {
        var tenantA = $"club-{Guid.NewGuid():N}";
        var tenantB = $"club-{Guid.NewGuid():N}";
        var inA = await StoreUserAsync();
        var inB = await StoreUserAsync();
        var groupId = await StoreGroupAsync(tenant: tenantA);
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var slug in new[] { tenantA, tenantB })
                s.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = inA, TenantSlug = tenantA, Status = MembershipStatus.Active });
            s.Store(new Membership { Id = Guid.NewGuid(), UserId = inB, TenantSlug = tenantB, Status = MembershipStatus.Active });
            await s.SaveChangesAsync(Ct);
        }
        var client = await ClientAsync(tenantA);

        var own = await client.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { userId = inA }, Ct);
        own.StatusCode.Should().Be(HttpStatusCode.OK, "the group is visible in the caller's tenant, so a 404 below can only come from the user check");
        var other = await client.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { userId = inB }, Ct);

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var members = (await LoadGroupAsync(groupId, tenantA)).UserIds;
        members.Should().HaveCount(1);
        members.Should().Contain(inA);
        members.Should().NotContain(inB);
    }

    [Fact]
    public async Task Removing_a_user_from_a_group_that_does_not_exist_answers_404_and_writes_no_audit_row()
    {
        var groupId = Guid.NewGuid();
        var userId = await StoreUserAsync(groupId);
        var client = await ClientAsync();

        var response = await client.DeleteAsync($"/api/users/{userId}/groups/{groupId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GroupRemovedRowsAsync(userId)).Should().Be(0);
        (await LoadUserAsync(userId)).GroupIds.Should().Contain(groupId, "a refused call changes nothing");
    }

    [Fact]
    public async Task Removing_a_user_from_a_deleted_group_answers_404_and_writes_no_audit_row()
    {
        var userId = await StoreUserAsync();
        var groupId = await StoreGroupAsync(deleted: true, [userId]);
        await LinkUserAsync(userId, groupId);
        var client = await ClientAsync();

        var response = await client.DeleteAsync($"/api/users/{userId}/groups/{groupId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GroupRemovedRowsAsync(userId)).Should().Be(0);
        (await LoadUserAsync(userId)).GroupIds.Should().Contain(groupId, "a refused call changes nothing");
        (await LoadGroupAsync(groupId)).UserIds.Should().Contain(userId);
    }

    [Fact]
    public async Task Removing_a_user_from_a_live_group_still_removes_both_sides_and_writes_one_audit_row()
    {
        var userId = await StoreUserAsync();
        var groupId = await StoreGroupAsync(deleted: false, [userId]);
        await LinkUserAsync(userId, groupId);
        var client = await ClientAsync();

        var response = await client.DeleteAsync($"/api/users/{userId}/groups/{groupId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await GroupRemovedRowsAsync(userId)).Should().Be(1);
        (await LoadUserAsync(userId)).GroupIds.Should().NotContain(groupId);
        (await LoadGroupAsync(groupId)).UserIds.Should().NotContain(userId);
    }

    private async Task<HttpClient> ClientAsync(string? tenant = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private async Task<Guid> StoreUserAsync(params Guid[] groupIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = "member" + Guid.NewGuid().ToString("n")[..10],
            Email = $"{Guid.NewGuid():n}@example.com",
            PasswordHash = "x",
            GroupIds = [.. groupIds],
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);
        return user.Id;
    }

    private async Task<Guid> StoreGroupAsync(bool deleted = false, Guid[]? userIds = null, string? tenant = null)
    {
        userIds ??= [];
        await using var session = tenant is null
            ? _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession()
            : _factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        var group = new UserGroup
        {
            Id = Guid.NewGuid(),
            Name = "g" + Guid.NewGuid().ToString("n")[..8],
            UserIds = [.. userIds],
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        session.Store(group);
        await session.SaveChangesAsync(Ct);
        return group.Id;
    }

    private async Task LinkUserAsync(Guid userId, Guid groupId)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var user = await session.LoadAsync<User>(userId, Ct);
        user!.GroupIds.Add(groupId);
        session.Store(user);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<UserGroup> LoadGroupAsync(Guid id, string? tenant = null)
    {
        await using var session = tenant is null
            ? _factory.Services.GetRequiredService<IDocumentStore>().QuerySession()
            : _factory.Services.GetRequiredService<IDocumentStore>().QuerySession(tenant);
        return (await session.LoadAsync<UserGroup>(id, Ct))!;
    }

    private async Task<User> LoadUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(id, Ct))!;
    }

    private async Task<int> GroupRemovedRowsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var target = userId.ToString();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .CountAsync(e => e.Action == "user.group.removed" && e.TargetId == target, Ct);
    }
}
