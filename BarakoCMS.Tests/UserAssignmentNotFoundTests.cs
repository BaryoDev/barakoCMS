using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Assigning a role or a group to an id that is not a user must be a 404, and must write nothing.
/// </summary>
/// <remarks>
/// Both endpoints carried a "load or create user (for testing, we'll create if not exists)" branch
/// into production. On a miss they stored a User with a synthesized <c>user_{guid}@example.com</c>
/// and no password hash, holding the role, and answered "Role assigned to user successfully". A
/// mistyped id therefore left a ghost identity row behind while the real account still lacked the
/// role, and the caller was told it had worked. The role id was never checked at all, so a mistyped
/// role also reported success and granted nothing.
/// </remarks>
[Collection("Sequential")]
public class UserAssignmentNotFoundTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public UserAssignmentNotFoundTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.CreateToken(roles: new[] { "SuperAdmin" }));
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"real-{id:N}",
            Email = $"real-{id:N}@test.com",
        });
        await session.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> CreateRoleAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = $"role-{Guid.NewGuid():N}" };
        session.Store(role);
        await session.SaveChangesAsync();
        return role.Id;
    }

    private async Task<Guid> CreateGroupAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var group = new UserGroup { Id = Guid.NewGuid(), Name = $"group-{Guid.NewGuid():N}" };
        session.Store(group);
        await session.SaveChangesAsync();
        return group.Id;
    }

    private async Task<User?> LoadUserAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<User>(id);
    }

    [Fact]
    public async Task Assigning_a_role_to_an_unknown_user_is_a_404()
    {
        var roleId = await CreateRoleAsync();
        var ghostId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync($"/api/users/{ghostId}/roles", new { roleId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a mistyped user id is a missing user, not a user to invent");
    }

    [Fact]
    public async Task Assigning_a_role_to_an_unknown_user_does_not_fabricate_one()
    {
        var roleId = await CreateRoleAsync();
        var ghostId = Guid.NewGuid();

        await _client.PostAsJsonAsync($"/api/users/{ghostId}/roles", new { roleId });

        var fabricated = await LoadUserAsync(ghostId);
        fabricated.Should().BeNull(
            "the endpoint used to store a user_{guid}@example.com identity with no password hash, holding the role");
    }

    [Fact]
    public async Task Assigning_an_unknown_role_to_a_real_user_is_a_404()
    {
        var userId = await CreateUserAsync();

        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "granting a role that does not exist grants nothing, so reporting success is a lie");

        var user = await LoadUserAsync(userId);
        user!.RoleIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Assigning_a_real_role_to_a_real_user_still_works()
    {
        var userId = await CreateUserAsync();
        var roleId = await CreateRoleAsync();

        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await LoadUserAsync(userId);
        user!.RoleIds.Should().Contain(roleId);
    }

    [Fact]
    public async Task Assigning_a_group_to_an_unknown_user_is_a_404_and_fabricates_nothing()
    {
        var groupId = await CreateGroupAsync();
        var ghostId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync($"/api/users/{ghostId}/groups", new { groupId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadUserAsync(ghostId)).Should().BeNull();
    }

    [Fact]
    public async Task Assigning_an_unknown_group_to_a_real_user_is_a_404()
    {
        var userId = await CreateUserAsync();

        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var user = await LoadUserAsync(userId);
        user!.GroupIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Assigning_a_real_group_to_a_real_user_still_works()
    {
        var userId = await CreateUserAsync();
        var groupId = await CreateGroupAsync();

        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await LoadUserAsync(userId);
        user!.GroupIds.Should().Contain(groupId);
    }
}
