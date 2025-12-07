using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FastEndpoints.Security;

namespace BarakoCMS.Tests.Features.Roles;

[Collection("Sequential")]
public class RoleApiTests : IClassFixture<IntegrationTestFixture>
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public RoleApiTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
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
    public async Task POST_Roles_WithValidData_ShouldCreateRole()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var request = new
        {
            name = "Content Editor",
            description = "Can edit own articles",
            permissions = new[]
            {
                new
                {
                    contentTypeSlug = "article",
                    create = new { enabled = true },
                    read = new { enabled = true },
                    update = new
                    {
                        enabled = true,
                        conditions = new Dictionary<string, object>
                        {
                            ["author"] = new { _eq = "$CURRENT_USER" }
                        }
                    },
                    delete = new { enabled = false }
                }
            },
            systemCapabilities = new[] { "view_analytics" }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/roles", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        result.Should().NotBeNull();
        result!.Id.Should().NotBeEmpty();
    }

    [Fact]
    public async Task POST_Roles_WithoutAuth_ShouldReturn401()
    {
        // Arrange
        var request = new { name = "Test Role" };
        _client.DefaultRequestHeaders.Authorization = null;

        // Act
        var response = await _client.PostAsJsonAsync("/api/roles", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_Roles_ShouldReturnAllRoles()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create a role first
        await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "Test Role for List",
            description = "Test",
            permissions = new object[] { }
        });

        // Act
        var response = await _client.GetAsync("/api/roles");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var roles = await response.Content.ReadFromJsonAsync<List<barakoCMS.Models.Role>>();
        roles.Should().NotBeNull();
        roles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GET_RolesById_ExistingRole_ShouldReturnRole()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create a role first
        var createResponse = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "Test Role for Get",
            description = "Test description",
            permissions = new object[] { }
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        var roleId = createResult!.Id;

        // Act
        var response = await _client.GetAsync($"/api/roles/{roleId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var role = await response.Content.ReadFromJsonAsync<barakoCMS.Models.Role>();
        role.Should().NotBeNull();
        role!.Name.Should().Be("Test Role for Get");
    }

    [Fact]
    public async Task GET_RolesById_NonExistent_ShouldReturn404()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.GetAsync($"/api/roles/{nonExistentId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PUT_Roles_ShouldUpdateRole()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create a role first
        var createResponse = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "Original Name",
            description = "Original description",
            permissions = new object[] { }
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        var roleId = createResult!.Id;

        // Act
        var updateRequest = new
        {
            id = roleId,
            name = "Updated Name",
            description = "Updated description",
            permissions = new object[] { }
        };
        var response = await _client.PutAsJsonAsync($"/api/roles/{roleId}", updateRequest);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the update
        var getResponse = await _client.GetAsync($"/api/roles/{roleId}");
        var updatedRole = await getResponse.Content.ReadFromJsonAsync<barakoCMS.Models.Role>();
        updatedRole!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task DELETE_Roles_ShouldDeleteRole()
    {
        // Arrange
        var token = CreateAdminToken();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Create a role first
        var createResponse = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = "Role to Delete",
            description = "Will be deleted",
            permissions = new object[] { }
        });
        var createResult = await createResponse.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>();
        var roleId = createResult!.Id;

        // Act
        var response = await _client.DeleteAsync($"/api/roles/{roleId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify deletion
        var getResponse = await _client.GetAsync($"/api/roles/{roleId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
