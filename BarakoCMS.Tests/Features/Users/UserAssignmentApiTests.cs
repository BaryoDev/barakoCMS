using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FastEndpoints.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Users;

[Collection("Sequential")]
public class UserAssignmentApiTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public UserAssignmentApiTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    // A user that exists, holding the seeded SuperAdmin role. The capability gate answers from the
    // stored user's roles, not from the claim.
    private Task<string> CreateAdminToken() => _fixture.StoredUserTokenAsync("SuperAdmin");

    /// <summary>
    /// A real user to assign things to.
    /// </summary>
    /// <remarks>
    /// These tests used to post a bare <c>Guid.NewGuid()</c>, because the assign endpoints
    /// fabricated a user on a miss and answered 200. That is the defect in #297, so the tests that
    /// depended on it have to stop depending on it.
    /// </remarks>
    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"assignable-{id:N}",
            Email = $"assignable-{id:N}@test.com",
        });
        await session.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task POST_AssignRoleToUser_ShouldAddRole()
    {
        // Arrange
        var token = await CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create role
        var roleResponse = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "Editor",
            description = "Content Editor"
        });

        if (!roleResponse.IsSuccessStatusCode)
        {
            var error = await roleResponse.Content.ReadAsStringAsync();
            throw new Exception($"Create Role failed: {roleResponse.StatusCode}, {error}");
        }

        var role = await roleResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();

        var userId = await CreateUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId = role!.Id });

        // Assert
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new Exception($"Request failed with {response.StatusCode}. Content: {content}");
        }
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DELETE_RemoveRoleFromUser_ShouldRemoveRole()
    {
        // Arrange
        var token = await CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var roleResponse = await _client.PostAsJsonAsync("/api/roles", new { name = "Viewer" });
        var role = await roleResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        var userId = await CreateUserAsync();

        // Assign role first
        await _client.PostAsJsonAsync($"/api/users/{userId}/roles", new { roleId = role!.Id });

        // Act
        var response = await _client.DeleteAsync($"/api/users/{userId}/roles/{role.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task POST_AddUserToGroup_ShouldAddToGroup()
    {
        // Arrange
        var token = await CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var groupResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Engineering",
            description = "Eng team"
        });
        var group = await groupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var userId = await CreateUserAsync();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = group!.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DELETE_RemoveUserFromGroup_ShouldRemoveFromGroup()
    {
        // Arrange
        var token = await CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var groupResponse = await _client.PostAsJsonAsync("/api/user-groups", new { name = "HR" });
        var group = await groupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var userId = await CreateUserAsync();

        // Add user first
        await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = group!.Id });

        // Act
        var response = await _client.DeleteAsync($"/api/users/{userId}/groups/{group.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
