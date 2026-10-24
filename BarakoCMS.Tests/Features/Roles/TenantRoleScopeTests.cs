using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// The roles routes and member role assignment reach the caller's tenant's roles and the platform
/// roles, and nothing of another tenant's. A platform role changes only with manage_platform_roles
/// (#913).
/// </summary>
[Collection("Integration1")]
public class TenantRoleScopeTests
{
    private readonly IntegrationTestFixture _fixture;
    private static int _ip;

    public TenantRoleScopeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task StoreAsync<T>(params T[] documents) where T : notnull
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(documents);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Role> LoadAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var role = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Role>(id, Ct);
        role.Should().NotBeNull();
        return role!;
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task<Role> RoleAsync(string? tenant, bool platform = false, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"r-{Guid.NewGuid():N}"[..18],
            Description = "before",
            TenantSlug = tenant,
            IsPlatform = platform,
            SystemCapabilities = [.. capabilities],
        };
        await StoreAsync(role);
        return role;
    }

    /// <summary>Two tenants, and a member of the first whose tenant role there carries manage_roles and manage_tenant_members.</summary>
    private async Task<(string A, string B, HttpClient Manager, Role ManagerRole)> TenantsAsync()
    {
        var (a, b) = (Slug("ra"), Slug("rb"));
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = a, Name = a },
            new Tenant { Id = Guid.NewGuid(), Slug = b, Name = b });
        _fixture.Services.GetRequiredService<ITenantDomainSource>().Invalidate();

        var managerRole = await RoleAsync(a, false, SystemCapabilities.ManageRoles, SystemCapabilities.ManageTenantMembers);
        var userId = Guid.NewGuid();
        await StoreAsync(new User { Id = userId, Username = $"rm{userId:N}"[..20], Email = $"rm{userId:N}@example.com" });
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = userId, TenantSlug = a, Status = MembershipStatus.Active, RoleIds = [managerRole.Id],
        });

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(
            [managerRole.Name], userId.ToString(), new Dictionary<string, string> { ["tenant"] = a }));
        client.DefaultRequestHeaders.Add("X-Tenant", a);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:913:{Interlocked.Increment(ref _ip):x}::1");
        return (a, b, client, managerRole);
    }

    private async Task<HttpClient> PlatformAdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static object Body(Role role, string description, params string[] capabilities) => new
    {
        name = role.Name,
        description,
        permissions = Array.Empty<object>(),
        systemCapabilities = capabilities.Length == 0 ? role.SystemCapabilities.ToArray() : capabilities,
    };

    private static async Task<List<Guid>> ListedAsync(HttpClient client)
    {
        var ids = new List<Guid>();
        for (var page = 1; page < 200; page++)
        {
            var res = await client.GetAsync($"/api/roles?page={page}&pageSize=100", Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
            var items = (await res.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").GetGuid()).ToList();
            ids.AddRange(items);
            if (items.Count < 100)
            {
                break;
            }
        }

        return ids;
    }

    [Fact]
    public async Task A_tenant_role_manager_edits_their_own_tenants_role_and_gets_404_for_another_tenants()
    {
        var (a, b, manager, _) = await TenantsAsync();
        var own = await RoleAsync(a);
        var theirs = await RoleAsync(b);

        var edited = await manager.PutAsJsonAsync($"/api/roles/{own.Id}", Body(own, "after"), Ct);
        edited.StatusCode.Should().Be(HttpStatusCode.OK, "the control: " + await edited.Content.ReadAsStringAsync(Ct));
        (await LoadAsync(own.Id)).Description.Should().Be("after");

        (await manager.PutAsJsonAsync($"/api/roles/{theirs.Id}", Body(theirs, "after"), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.GetAsync($"/api/roles/{theirs.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.DeleteAsync($"/api/roles/{theirs.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadAsync(theirs.Id)).Description.Should().Be("before", "nothing of tenant B's changed");
    }

    [Fact]
    public async Task The_role_list_holds_the_tenants_roles_and_the_platform_roles_and_not_another_tenants()
    {
        var (a, b, manager, managerRole) = await TenantsAsync();
        var own = await RoleAsync(a);
        var theirs = await RoleAsync(b);

        var listed = await ListedAsync(manager);

        listed.Should().Contain([own.Id, managerRole.Id, SystemRoles.AdminRoleId]);
        listed.Should().NotContain(theirs.Id);

        (await ListedAsync(await PlatformAdminAsync())).Should().Contain([own.Id, theirs.Id], "a platform administrator sees every tenant's roles");
    }

    [Fact]
    public async Task A_platform_role_changes_only_with_manage_platform_roles()
    {
        var (_, _, manager, _) = await TenantsAsync();
        var shared = await RoleAsync(null, platform: true);
        var admin = await LoadAsync(SystemRoles.AdminRoleId);

        (await manager.PutAsJsonAsync($"/api/roles/{SystemRoles.AdminRoleId}", Body(admin, admin.Description), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Admin is seeded for every tenant");
        (await manager.PutAsJsonAsync($"/api/roles/{shared.Id}", Body(shared, "after"), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.DeleteAsync($"/api/roles/{shared.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await manager.GetAsync($"/api/roles/{shared.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "a platform role is still readable");
        (await LoadAsync(shared.Id)).Description.Should().Be("before");

        var platform = await PlatformAdminAsync();
        var edited = await platform.PutAsJsonAsync($"/api/roles/{shared.Id}", Body(shared, "after"), Ct);
        edited.StatusCode.Should().Be(HttpStatusCode.OK, "the control: " + await edited.Content.ReadAsStringAsync(Ct));
        (await LoadAsync(shared.Id)).Description.Should().Be("after");
    }

    [Fact]
    public async Task A_role_created_in_a_tenant_belongs_to_it_and_cannot_reach_past_it()
    {
        var (a, _, manager, managerRole) = await TenantsAsync();
        var name = $"Clerk-{Guid.NewGuid():N}"[..16];

        var created = await manager.PostAsJsonAsync("/api/roles", new { name, description = "d", systemCapabilities = new[] { "view_audit_log" } }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var stored = await LoadAsync(id);
        stored.TenantSlug.Should().Be(a);
        stored.IsPlatform.Should().BeFalse();

        (await manager.PostAsJsonAsync("/api/roles", new { name, description = "d" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "the name is taken in this tenant");

        (await manager.PostAsJsonAsync("/api/roles", new { name = name + "p", description = "d", isPlatform = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a tenant does not make platform roles");

        (await manager.PostAsJsonAsync("/api/roles", new { name = name + "t", description = "d", systemCapabilities = new[] { SystemCapabilities.ManageTenants } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "manage_tenants reaches every tenant");

        var escalate = await manager.PutAsJsonAsync($"/api/roles/{managerRole.Id}",
            Body(managerRole, managerRole.Description, [.. managerRole.SystemCapabilities, SystemCapabilities.All]), Ct);
        escalate.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the manager cannot widen their own role past the tenant");
        (await LoadAsync(managerRole.Id)).SystemCapabilities.Should().NotContain(SystemCapabilities.All);
    }

    [Fact]
    public async Task Member_role_assignment_offers_and_accepts_the_tenants_roles_and_not_another_tenants()
    {
        var (a, b, manager, _) = await TenantsAsync();
        var own = await RoleAsync(a);
        var theirs = await RoleAsync(b);

        var offered = await manager.GetAsync("/api/tenants/members/roles?page=1&pageSize=1000", Ct);
        offered.StatusCode.Should().Be(HttpStatusCode.OK, await offered.Content.ReadAsStringAsync(Ct));
        var ids = (await offered.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().Contain([own.Id, SystemRoles.AdminRoleId]);
        ids.Should().NotContain(theirs.Id);

        var accepted = await manager.PostAsJsonAsync("/api/tenants/members",
            new { email = $"m-{Guid.NewGuid():N}@example.com", roleIds = new[] { own.Id } }, Ct);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, "the control: " + await accepted.Content.ReadAsStringAsync(Ct));

        var refused = await manager.PostAsJsonAsync("/api/tenants/members",
            new { email = $"m-{Guid.NewGuid():N}@example.com", roleIds = new[] { theirs.Id } }, Ct);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("do not exist", "another tenant's role is answered as one that does not exist");
    }

    [Fact]
    public async Task A_role_a_tenant_names_for_sign_up_or_MFA_cannot_be_deleted()
    {
        var slug = Slug("rd");
        var signUpRole = await RoleAsync(slug);
        var mfaRole = await RoleAsync(slug);
        var unused = await RoleAsync(slug);
        await StoreAsync(new Tenant
        {
            Id = Guid.NewGuid(), Slug = slug, Name = slug,
            SignUp = TenantSignUp.Open, SignUpRoleId = signUpRole.Id,
            SignIn = new TenantSignInPolicy { MfaRoleIds = [mfaRole.Id] },
        });
        var admin = await PlatformAdminAsync();

        (await admin.DeleteAsync($"/api/roles/{unused.Id}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the control");

        foreach (var role in new[] { signUpRole, mfaRole })
        {
            var refused = await admin.DeleteAsync($"/api/roles/{role.Id}", Ct);
            var body = await refused.Content.ReadAsStringAsync(Ct);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
            body.Should().Contain(slug, "the tenant to change is named");
            (await LoadAsync(role.Id)).Id.Should().Be(role.Id);
        }
    }

    [Fact]
    public async Task A_membership_holding_another_tenants_role_gets_nothing_from_it()
    {
        var (a, b, _, _) = await TenantsAsync();
        var own = await RoleAsync(a);
        var theirs = await RoleAsync(b);
        var user = new User { Id = Guid.NewGuid(), Username = $"x{Guid.NewGuid():N}"[..20], Email = $"x{Guid.NewGuid():N}@example.com" };
        await StoreAsync(user);
        await StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = user.Id, TenantSlug = a, Status = MembershipStatus.Active, RoleIds = [own.Id, theirs.Id, SystemRoles.UserRoleId],
        });

        using var scope = _fixture.Services.CreateScope();
        var effective = await MembershipRoles.EffectiveRoleIdsAsync(
            scope.ServiceProvider.GetRequiredService<IQuerySession>(), user, a, Ct);

        effective.Should().BeEquivalentTo([own.Id, SystemRoles.UserRoleId]);
    }
}
