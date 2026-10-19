using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Who may restore or erase a deleted role, which deleted rows a name reuse takes, and the places
/// that build a caller's roles ignoring a deleted role or group (#625).
/// </summary>
[Collection("Sequential")]
public class RoleSoftDeleteScopeTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public RoleSoftDeleteScopeTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> TenantAsync()
    {
        var slug = Unique("sd");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    private async Task<HttpClient> TenantAdminAsync(string slug)
    {
        var userId = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{username}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(["Admin"], userId.ToString(),
                new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    private async Task<Role?> LoadRoleAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
    }

    [Fact]
    public async Task A_tenant_admin_cannot_restore_a_deleted_platform_role()
    {
        var tenant = await TenantAsync();
        var admin = await TenantAdminAsync(tenant);
        var platform = new Role { Id = Guid.NewGuid(), Name = Unique("platform"), IsPlatform = true, DeletedAt = DateTime.UtcNow };
        await StoreAsync(platform);

        var response = await admin.PostAsync($"/api/roles/{platform.Id}/restore", null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LoadRoleAsync(platform.Id))!.DeletedAt.Should().NotBeNull("the refused restore changed nothing");
    }

    [Theory]
    [InlineData("POST", "restore")]
    [InlineData("DELETE", "erase")]
    public async Task Another_tenants_deleted_role_is_not_found_to_restore_or_erase(string method, string action)
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var admin = await TenantAdminAsync(mine);
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("theirs"), TenantSlug = theirs, DeletedAt = DateTime.UtcNow };
        await StoreAsync(role);

        var response = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"/api/roles/{role.Id}/{action}"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadRoleAsync(role.Id))!.DeletedAt.Should().NotBeNull("the other tenant's deleted role is untouched");
    }

    [Fact]
    public async Task A_new_role_erases_only_the_deleted_role_of_its_own_owner_that_holds_the_name()
    {
        var mine = await TenantAsync();
        var theirs = await TenantAsync();
        var admin = await TenantAdminAsync(mine);
        var name = Unique("shared");
        var own = new Role { Id = Guid.NewGuid(), Name = name, TenantSlug = mine, DeletedAt = DateTime.UtcNow };
        var other = new Role { Id = Guid.NewGuid(), Name = name, TenantSlug = theirs, DeletedAt = DateTime.UtcNow };
        await StoreAsync(own, other);

        var response = await admin.PostAsJsonAsync("/api/roles", new { name, description = "d", permissions = Array.Empty<object>(), systemCapabilities = Array.Empty<string>() }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var fresh = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await LoadRoleAsync(own.Id)).Should().BeNull("the same owner's deleted role gave the name up");
        (await LoadRoleAsync(other.Id)).Should().NotBeNull("another tenant's deleted role keeps its name");
        (await LoadRoleAsync(fresh))!.TenantSlug.Should().Be(mine);
    }

    [Fact]
    public async Task A_deleted_group_grants_no_role_to_its_members_and_a_restored_one_grants_it_again()
    {
        var slug = Unique("grp");
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = Unique("viaGroup"), TenantSlug = null,
            Permissions = [new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User { Id = Guid.NewGuid(), Username = Unique("m"), Email = $"{Guid.NewGuid():n}@example.com", PasswordHash = "x" };
        var group = new UserGroup { Id = Guid.NewGuid(), Name = Unique("g"), RoleIds = [role.Id], UserIds = [user.Id] };
        await StoreAsync(role, user, group);

        (await HoldsAsync(user, role.Id, slug)).Should().BeTrue("the group grants the role, so the denial below is about deletion");

        group.DeletedAt = DateTime.UtcNow;
        await StoreAsync(group);
        (await HoldsAsync(user, role.Id, slug)).Should().BeFalse("a deleted group grants nothing to its members");

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin", "Admin"));
        (await admin.PostAsync($"/api/user-groups/{group.Id}/restore", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await CanReadAsync(user, slug)).Should().BeTrue("restoring the group grants again, with no manual cache reset");
    }

    private async Task<bool> HoldsAsync(User user, Guid roleId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IPermissionResolver>().InvalidateAllPermissions();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var ids = await MembershipRoles.EffectiveRoleIdsAsync(session, user, Tenant.DefaultSlug, Ct);
        var canRead = await CanReadAsync(user, slug);
        return ids.Contains(roleId) && canRead;
    }

    private async Task<bool> CanReadAsync(User user, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<User>(user.Id, Ct);
        return await scope.ServiceProvider.GetRequiredService<IPermissionResolver>()
            .CanPerformActionAsync(stored!, slug, "read", cancellationToken: Ct);
    }

    [Fact]
    public async Task A_deleted_seeded_role_comes_back_on_start_as_it_did_when_a_delete_removed_the_row()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var seeded = await session.LoadAsync<Role>(SystemRoles.UserRoleId, Ct);
            seeded.Should().NotBeNull("the User role is seeded at start");
            seeded!.DeletedAt = DateTime.UtcNow;
            session.Store(seeded);
            await session.SaveChangesAsync(Ct);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            await barakoCMS.Data.DataSeeder.SeedRolesAsync(session);
            await session.SaveChangesAsync(Ct);
        }

        var back = await LoadRoleAsync(SystemRoles.UserRoleId);
        back.Should().NotBeNull();
        back!.DeletedAt.Should().BeNull();
        back.Name.Should().Be("User");
    }

    [Fact]
    public async Task A_token_minted_after_a_role_is_deleted_does_not_carry_it()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("tokenrole"), TenantSlug = null };
        var user = new User { Id = Guid.NewGuid(), Username = Unique("tk"), Email = $"{Guid.NewGuid():n}@example.com", PasswordHash = "x", RoleIds = [role.Id] };
        await StoreAsync(role, user);

        (await IssuedRolesAsync(user)).Should().Contain(role.Name, "a live role is carried, so the absence below is about deletion");

        role.DeletedAt = DateTime.UtcNow;
        await StoreAsync(role);
        (await IssuedRolesAsync(user)).Should().NotContain(role.Name);
    }

    private async Task<IReadOnlyList<string>> IssuedRolesAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<ITokenIssuer>();
        var result = await issuer.IssueAccessTokenAsync(user, Tenant.DefaultSlug, null, Ct);
        result.Allowed.Should().BeTrue();
        return result.Roles;
    }

    [Fact]
    public async Task An_api_key_does_not_carry_a_role_its_owner_lost_to_deletion()
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Unique("keyrole"), TenantSlug = null };
        var user = new User { Id = Guid.NewGuid(), Username = Unique("ak"), Email = $"{Guid.NewGuid():n}@example.com", PasswordHash = "x", RoleIds = [role.Id] };
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        await StoreAsync(role, user, new ApiKey
        {
            Id = Guid.NewGuid(), Name = "t", KeyHash = ApiKeyService.Hash(secret), Prefix = secret[..12],
            UserId = user.Id, TenantSlug = Tenant.DefaultSlug, Scopes = ["content:read"],
        });

        (await KeyRolesAsync(secret)).Should().Contain(role.Name, "a live role is carried, so the absence below is about deletion");

        role.DeletedAt = DateTime.UtcNow;
        await StoreAsync(role);
        (await KeyRolesAsync(secret)).Should().NotContain(role.Name);
    }

    private async Task<List<string>> KeyRolesAsync(string secret)
    {
        using var scope = _factory.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + secret;
        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(context, ApiKeyAuthenticationHandler.SchemeName);
        result.Succeeded.Should().BeTrue();
        return result.Principal!.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
    }
}
