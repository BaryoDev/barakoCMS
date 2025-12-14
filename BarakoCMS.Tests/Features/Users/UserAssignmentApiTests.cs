using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FastEndpoints.Security;

namespace BarakoCMS.Tests.Features.Users;

[Collection("Sequential")]
public class UserAssignmentApiTests : IClassFixture<IntegrationTestFixture>
{
    private readonly HttpClient _client;

    public UserAssignmentApiTests(IntegrationTestFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    private string CreateAdminToken()
    {
        return JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            issuer: "BarakoTest",
            audience: "BarakoClient",
            privileges: u =>
            {
                u.Roles.Add("SuperAdmin");
                u.Claims.Add(new(System.Security.Claims.ClaimTypes.Role, "SuperAdmin"));
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });
    }

    [Fact]
    public async Task POST_AssignRoleToUser_ShouldAddRole()
    {
        // Arrange
        var token = CreateAdminToken();
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

        var userId = Guid.NewGuid();

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
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var roleResponse = await _client.PostAsJsonAsync("/api/roles", new { name = "Viewer" });
        var role = await roleResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        var userId = Guid.NewGuid();

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
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var groupResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Engineering",
            description = "Eng team"
        });
        var group = await groupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var userId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = group!.Id });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DELETE_RemoveUserFromGroup_ShouldRemoveFromGroup()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var groupResponse = await _client.PostAsJsonAsync("/api/user-groups", new { name = "HR" });
        var group = await groupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var userId = Guid.NewGuid();

        // Add user first
        await _client.PostAsJsonAsync($"/api/users/{userId}/groups", new { groupId = group!.Id });

        // Act
        var response = await _client.DeleteAsync($"/api/users/{userId}/groups/{group.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
