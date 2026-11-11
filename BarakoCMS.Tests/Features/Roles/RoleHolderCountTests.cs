using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Roles;

/// <summary>
/// The role list says how many people hold each role of the page, in one statement for the page,
/// and only to a caller who may see people or manage roles.
/// </summary>
[Collection("Integration1")]
public class RoleHolderCountTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly CapturingMartenLogger _logger = new();
    private readonly IServiceProvider _services;
    private readonly Func<HttpClient> _newClient;
    private static int _ip;

    public RoleHolderCountTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        var derived = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.ConfigureMarten(options => options.Logger(_logger))));
        _services = derived.Services;
        _newClient = derived.CreateClient;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task StoreAsync<T>(params T[] documents) where T : notnull
    {
        using var scope = _services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(documents);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<Role> RoleAsync(string? tenant, bool platform = false, params string[] capabilities)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"r-{Guid.NewGuid():N}"[..18],
            TenantSlug = tenant,
            IsPlatform = platform,
            SystemCapabilities = [.. capabilities],
        };
        await StoreAsync(role);
        return role;
    }

    private async Task<Guid> UserAsync(List<Guid>? roles = null, List<Guid>? groups = null)
    {
        var id = Guid.NewGuid();
        await StoreAsync(new User
        {
            Id = id,
            Username = $"rh{id:N}"[..20],
            Email = $"rh{id:N}@example.com",
            RoleIds = roles ?? [],
            GroupIds = groups ?? [],
        });
        return id;
    }

    private Task MemberAsync(Guid userId, string tenant, Guid roleId, MembershipStatus status = MembershipStatus.Active) =>
        StoreAsync(new Membership
        {
            Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenant, Status = status, RoleIds = [roleId],
        });

    private async Task GroupAsync(string tenant, Guid roleId, List<Guid>? userIds = null, bool deleted = false, Guid? id = null)
    {
        var store = _services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new UserGroup
        {
            Id = id ?? Guid.NewGuid(),
            Name = $"g-{Guid.NewGuid():N}"[..12],
            RoleIds = [roleId],
            UserIds = userIds ?? [],
            DeletedAt = deleted ? DateTime.UtcNow : null,
        });
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>A tenant, and a member of it whose tenant role carries manage_roles, signed in to it.</summary>
    private async Task<(string Tenant, HttpClient Manager)> ManagerOfAsync()
    {
        var tenant = Slug("rh");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant });
        _services.GetRequiredService<ITenantDomainSource>().Invalidate();

        var managerRole = await RoleAsync(tenant, false, SystemCapabilities.ManageRoles);
        var manager = await UserAsync();
        await MemberAsync(manager, tenant, managerRole.Id);
        return (tenant, ClientFor(manager, managerRole.Name, tenant));
    }

    private HttpClient ClientFor(Guid userId, string role, string tenant)
    {
        var client = _newClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(
            [role], userId.ToString(), new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:914:{Interlocked.Increment(ref _ip):x}::1");
        return client;
    }

    private async Task<Dictionary<Guid, JsonElement>> ListAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/roles?pageSize=100", Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.Clone());
    }

    private static int Holders(Dictionary<Guid, JsonElement> roles, Role role) =>
        roles[role.Id].GetProperty("holderCount").GetInt32();

    [Fact]
    public async Task A_tenant_role_counts_the_active_members_of_its_tenant_and_nobody_else()
    {
        var (tenant, manager) = await ManagerOfAsync();
        var other = Slug("ro");
        var role = await RoleAsync(tenant);
        foreach (var _ in Enumerable.Range(0, 3))
            await MemberAsync(await UserAsync(), tenant, role.Id);
        await MemberAsync(await UserAsync(), tenant, role.Id, MembershipStatus.Removed);
        await MemberAsync(await UserAsync(), other, role.Id);
        await UserAsync(roles: [role.Id]);

        var roles = await ListAsync(manager);

        Holders(roles, role).Should().Be(3, "removed members, another tenant's members and a role set on a user directly do not count");
    }

    [Fact]
    public async Task A_platform_role_counts_direct_group_and_membership_holders_once_each()
    {
        var (tenant, manager) = await ManagerOfAsync();
        var role = await RoleAsync(null, platform: true);

        var direct = await UserAsync(roles: [role.Id]);
        var viaMembership = await UserAsync();
        await MemberAsync(viaMembership, tenant, role.Id);
        await MemberAsync(direct, tenant, role.Id);
        var listed = await UserAsync();
        await GroupAsync(tenant, role.Id, userIds: [listed]);
        var groupId = Guid.NewGuid();
        await GroupAsync(tenant, role.Id, id: groupId);
        await UserAsync(groups: [groupId]);
        await GroupAsync(tenant, role.Id, userIds: [await UserAsync()], deleted: true);
        await MemberAsync(await UserAsync(), Slug("ro"), role.Id);

        var roles = await ListAsync(manager);

        Holders(roles, role).Should().Be(4, "direct (also a member: once), member, listed in a group, naming a group; not a deleted group or another tenant");
    }

    [Fact]
    public async Task A_role_nobody_holds_says_zero_and_not_null()
    {
        var (tenant, manager) = await ManagerOfAsync();
        var empty = await RoleAsync(tenant);
        var held = await RoleAsync(tenant);
        await MemberAsync(await UserAsync(), tenant, held.Id);

        var roles = await ListAsync(manager);

        Holders(roles, held).Should().Be(1, "the control: a count is produced at all");
        Holders(roles, empty).Should().Be(0);
    }

    private async Task<(int Memberships, int Total)> StatementsForAPageOfAsync(int roleCount)
    {
        var (tenant, manager) = await ManagerOfAsync();
        for (var i = 0; i < roleCount; i++)
        {
            var role = await RoleAsync(i % 2 == 0 ? tenant : null, platform: i % 2 != 0);
            await MemberAsync(await UserAsync(), tenant, role.Id);
        }

        _logger.Commands.Clear();
        var roles = await ListAsync(manager);
        roles.Values.Count(r => r.GetProperty("holderCount").GetInt32() > 0).Should().BeGreaterThanOrEqualTo(roleCount);

        var sql = _logger.Commands.Select(c => c.Sql).ToList();
        return (sql.Count(s => s.Contains("mt_doc_memberships") && s.Contains("jsonb_array_elements_text")),
                sql.Count(s => s.Contains("jsonb_array_elements_text")));
    }

    [Fact]
    public async Task The_statements_for_holder_counts_stay_flat_as_the_page_grows()
    {
        var small = await StatementsForAPageOfAsync(2);
        var large = await StatementsForAPageOfAsync(12);

        small.Should().Be((1, 1));
        large.Should().Be((1, 1));
    }

    [Fact]
    public async Task A_caller_who_may_neither_manage_roles_nor_list_users_is_refused_the_list()
    {
        var tenant = Slug("rh");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant });
        _services.GetRequiredService<ITenantDomainSource>().Invalidate();
        var plain = await RoleAsync(tenant);
        var user = await UserAsync();
        await MemberAsync(user, tenant, plain.Id);

        var res = await ClientFor(user, plain.Name, tenant).GetAsync("/api/roles?pageSize=100", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.Content.ReadAsStringAsync(Ct)).Should().NotContain("holderCount");
    }

    [Fact]
    public async Task A_caller_who_lists_users_and_not_roles_is_refused_the_role_list_and_gets_no_count()
    {
        var tenant = Slug("rh");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant });
        _services.GetRequiredService<ITenantDomainSource>().Invalidate();
        var usersOnly = await RoleAsync(tenant, false, SystemCapabilities.ManageUsers);
        var user = await UserAsync();
        await MemberAsync(user, tenant, usersOnly.Id);

        var res = await ClientFor(user, usersOnly.Name, tenant).GetAsync("/api/roles?pageSize=100", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the route still asks for manage_roles, so a count never reaches a caller the gate refuses");
    }
}
