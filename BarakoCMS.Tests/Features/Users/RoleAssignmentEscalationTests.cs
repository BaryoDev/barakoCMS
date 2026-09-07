using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Users;

/// <summary>
/// POST /api/users/{id}/roles is reachable with manage_user_membership, which the Admin role holds
/// (not manage_roles). Without a guard, an Admin assigns itself the SuperAdmin role and steps
/// outside the whole capability model. The per-tenant sibling refuses SuperAdmin outright; this
/// platform surface allows it, but only for a caller who already is SuperAdmin.
/// </summary>
[Collection("Sequential")]
public class RoleAssignmentEscalationTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;

    public RoleAssignmentEscalationTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();
    }

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User { Id = id, Username = $"target-{id:N}", Email = $"target-{id:N}@test.com" });
        await session.SaveChangesAsync();
        return id;
    }

    private void Auth(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task Admin_cannot_grant_the_SuperAdmin_role()
    {
        Auth(await _fixture.StoredUserTokenAsync("Admin"));
        var target = await CreateUserAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/users/{target}/roles", new { roleId = SystemRoles.SuperAdminRoleId });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "manage_user_membership must not be a path to SuperAdmin");

        // and the grant did not happen
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<User>(target);
        stored!.RoleIds.Should().NotContain(SystemRoles.SuperAdminRoleId);
    }

    [Fact]
    public async Task Admin_can_still_grant_an_ordinary_role()
    {
        Auth(await _fixture.StoredUserTokenAsync("Admin"));
        var target = await CreateUserAsync();

        // Admin lacks manage_roles, so it cannot create roles through the API; seed one directly.
        Guid roleId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            roleId = Guid.NewGuid();
            session.Store(new Role { Id = roleId, Name = $"ord-{roleId:N}", SystemCapabilities = new() { "view_monitoring" } });
            await session.SaveChangesAsync();
        }

        var res = await _client.PostAsJsonAsync($"/api/users/{target}/roles", new { roleId });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "the guard is specific to SuperAdmin, not all roles");
    }

    [Fact]
    public async Task SuperAdmin_may_grant_the_SuperAdmin_role()
    {
        Auth(await _fixture.StoredUserTokenAsync("SuperAdmin"));
        var target = await CreateUserAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/users/{target}/roles", new { roleId = SystemRoles.SuperAdminRoleId });

        res.StatusCode.Should().Be(HttpStatusCode.OK, "a SuperAdmin promoting another is legitimate");
    }
}
