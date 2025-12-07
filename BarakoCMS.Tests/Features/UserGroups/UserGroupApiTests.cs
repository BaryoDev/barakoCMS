using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FastEndpoints.Security;

namespace BarakoCMS.Tests.Features.UserGroups;

[Collection("Sequential")]
public class UserGroupApiTests : IClassFixture<IntegrationTestFixture>
{
    private readonly HttpClient _client;

    public UserGroupApiTests(IntegrationTestFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    private string CreateAdminToken()
    {
        return JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("SuperAdmin");
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });
    }

    [Fact]
    public async Task POST_UserGroups_WithValidData_ShouldCreateGroup()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            name = "Engineering Team",
            description = "All engineers"
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/user-groups", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        result.Should().NotBeNull();
        result!.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GET_UserGroups_ShouldReturnAllGroups()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create a group first
        await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Test Group",
            description = "Test"
        });

        // Act
        var response = await _client.GetAsync("/api/user-groups");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var groups = await response.Content.ReadFromJsonAsync<List<barakoCMS.Models.UserGroup>>();
        groups.Should().NotBeNull();
        groups.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GET_UserGroupsById_ExistingGroup_ShouldReturnGroup()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "HR Department",
            description = "Human Resources"
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var groupId = createResult!.Id;

        // Act
        var response = await _client.GetAsync($"/api/user-groups/{groupId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var group = await response.Content.ReadFromJsonAsync<barakoCMS.Models.UserGroup>();
        group.Should().NotBeNull();
        group!.Name.Should().Be("HR Department");
    }

    [Fact]
    public async Task PUT_UserGroups_ShouldUpdateGroup()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Original Name",
            description = "Original"
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var groupId = createResult!.Id;

        // Act
        var updateRequest = new
        {
            id = groupId,
            name = "Updated Name",
            description = "Updated description"
        };
        var response = await _client.PutAsJsonAsync($"/api/user-groups/{groupId}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify
        var getResponse = await _client.GetAsync($"/api/user-groups/{groupId}");
        var updatedGroup = await getResponse.Content.ReadFromJsonAsync<barakoCMS.Models.UserGroup>();
        updatedGroup!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task DELETE_UserGroups_ShouldDeleteGroup()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var createResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Group to Delete",
            description = "Will be deleted"
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var groupId = createResult!.Id;

        // Act
        var response = await _client.DeleteAsync($"/api/user-groups/{groupId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deletion
        var getResponse = await _client.GetAsync($"/api/user-groups/{groupId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task POST_AddUserToGroup_ShouldAddUser()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create group
        var createGroupResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Test Group",
            description = "Test"
        });
        var groupResult = await createGroupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var groupId = groupResult!.Id;

        var userId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsJsonAsync($"/api/user-groups/{groupId}/users", new { userId });

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify user added
        var getResponse = await _client.GetAsync($"/api/user-groups/{groupId}");
        var group = await getResponse.Content.ReadFromJsonAsync<barakoCMS.Models.UserGroup>();
        group!.UserIds.Should().Contain(userId);
    }

    [Fact]
    public async Task DELETE_RemoveUserFromGroup_ShouldRemoveUser()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create group with user
        var userId = Guid.NewGuid();
        var createGroupResponse = await _client.PostAsJsonAsync("/api/user-groups", new
        {
            name = "Test Group",
            description = "Test",
            userIds = new[] { userId }
        });
        var groupResult = await createGroupResponse.Content.ReadFromJsonAsync<barakoCMS.Features.UserGroups.Create.Response>();
        var groupId = groupResult!.Id;

        // Act
        var response = await _client.DeleteAsync($"/api/user-groups/{groupId}/users/{userId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify user removed
        var getResponse = await _client.GetAsync($"/api/user-groups/{groupId}");
        var group = await getResponse.Content.ReadFromJsonAsync<barakoCMS.Models.UserGroup>();
        group!.UserIds.Should().NotContain(userId);
    }
}
