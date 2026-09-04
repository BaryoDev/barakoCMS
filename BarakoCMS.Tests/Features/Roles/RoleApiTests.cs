using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using FastEndpoints.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Roles;

[Collection("Sequential")]
public class RoleApiTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public RoleApiTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    // A token for a user that exists. A capability gate answers from the stored user's roles, not
    // from the claim, and the legacy role-name fallback that used to paper over the difference is
    // off by default from 4.0.
    private Task<string> CreateAdminToken() => _fixture.StoredUserTokenAsync("SuperAdmin");

    [Fact]
    public async Task POST_Roles_WithValidData_ShouldCreateRole()
    {
        // Arrange
        var token = await CreateAdminToken();
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
        var token = await CreateAdminToken();
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
        var result = await response.Content.ReadFromJsonAsync<barakoCMS.Models.PaginatedResponse<barakoCMS.Models.Role>>();
        result.Should().NotBeNull();
        result!.Items.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GET_RolesById_ExistingRole_ShouldReturnRole()
    {
        // Arrange
        var token = await CreateAdminToken();
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
        var token = await CreateAdminToken();
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
        var token = await CreateAdminToken();
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
        var token = await CreateAdminToken();
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

    /// <summary>
    /// A role nobody holds globally, but which fifty tenant members hold through their memberships,
    /// cannot be deleted.
    /// </summary>
    /// <remarks>
    /// The guard only looked at <c>User.RoleIds</c>, which is not where a tenant member's roles live:
    /// <c>MembershipRoles.EffectiveRoleIdsAsync</c> unions the membership list into the global one,
    /// and <c>CreateTenantEndpoint</c> writes the membership list when it seeds a tenant admin. So the
    /// delete succeeded, every one of those memberships was left holding an id that resolves to
    /// nothing, and PermissionResolver denies rather than errors. See issue #290.
    /// </remarks>
    [Fact]
    public async Task A_role_held_only_through_a_membership_cannot_be_deleted()
    {
        var roleId = await CreateRoleAsync("membership-held");
        var tenant = "clubx-" + Guid.NewGuid().ToString("n")[..8];

        await StoreMembershipAsync(tenant, roleId);

        var response = await _client.DeleteAsync($"/api/roles/{roleId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain(tenant,
            "a refusal that does not say where the role is held cannot be acted on");

        (await _client.GetAsync($"/api/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the role must still be there after a refused delete");
    }

    /// <summary>
    /// The positive control: memberships exist, they just do not hold this role, so the delete goes
    /// through. Without it, a guard that refused every delete would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_role_no_membership_holds_is_still_deletable()
    {
        var otherRoleId = await CreateRoleAsync("held-elsewhere");
        var roleId = await CreateRoleAsync("held-by-nobody");

        await StoreMembershipAsync("clubz-" + Guid.NewGuid().ToString("n")[..8], otherRoleId);

        var response = await _client.DeleteAsync($"/api/roles/{roleId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/roles/{roleId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<Guid> CreateRoleAsync(string label)
    {
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await CreateAdminToken());

        var response = await _client.PostAsJsonAsync("/api/roles", new
        {
            name = $"{label}-{Guid.NewGuid():N}",
            description = label,
            permissions = new object[] { },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Roles.Create.Response>())!.Id;
    }

    private async Task StoreMembershipAsync(string tenantSlug, Guid roleId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<Marten.IDocumentSession>();

        session.Store(new barakoCMS.Models.Membership
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TenantSlug = tenantSlug,
            RoleIds = new List<Guid> { roleId },
        });

        await session.SaveChangesAsync();
    }
}
