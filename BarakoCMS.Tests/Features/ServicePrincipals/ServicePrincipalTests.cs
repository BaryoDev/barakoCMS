using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ServicePrincipals;

/// <summary>
/// A key that acts as a service principal takes its roles from the principal, outlives the person who
/// made it, and can never hold a platform role. Keys are presented on <c>GET /api/jobs</c>, which is
/// gated on the <c>view_jobs</c> capability and sits outside the content API, so the <c>cap:</c> scope
/// is what reaches it.
/// </summary>
[Collection("Integration1")]
public class ServicePrincipalTests
{
    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public ServicePrincipalTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NextIp() => $"203.0.113.{Interlocked.Increment(ref _ipCounter) % 250 + 1}"; // TEST-NET-3

    private sealed record Caller(Guid UserId, string Tenant, HttpClient Client);

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"sp-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<Role> RoleAsync(string tenant, string[] capabilities, bool platform = false)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"sp-role-{Guid.NewGuid():N}"[..20],
            Permissions = new(),
            SystemCapabilities = capabilities.ToList(),
            TenantSlug = platform ? null : tenant,
            IsPlatform = platform,
        };
        session.Store(role);
        await session.SaveChangesAsync(Ct);
        return role;
    }

    private async Task<Guid> MemberAsync(string tenant, params Guid[] roleIds)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"sp-{userId:N}"[..14], Email = $"{userId:N}@example.com" });
        session.Store(new Membership
        {
            Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenant,
            Status = MembershipStatus.Active, RoleIds = roleIds.ToList(),
        });
        await session.SaveChangesAsync(Ct);
        return userId;
    }

    // A key steward of the tenant: a stored user whose membership role carries manage_api_keys.
    private async Task<Caller> StewardAsync(string tenant, params string[] extraCapabilities)
    {
        var role = await RoleAsync(tenant, [SystemCapabilities.ManageApiKeys, .. extraCapabilities]);
        var userId = await MemberAsync(tenant, role.Id);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.CreateToken(
            roles: [role.Name], userId: userId.ToString(),
            additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return new Caller(userId, tenant, client);
    }

    private sealed record Principal(Guid Id, string Name);
    private sealed record Created(Guid Id, string Key);

    private async Task<HttpResponseMessage> CreatePrincipalRawAsync(Caller caller, params Guid[] roleIds) =>
        await caller.Client.PostAsJsonAsync("/api/service-principals",
            new { name = $"ci-{Guid.NewGuid():N}"[..10], roleIds }, Ct);

    private async Task<Principal> CreatePrincipalAsync(Caller caller, params Guid[] roleIds)
    {
        var res = await CreatePrincipalRawAsync(caller, roleIds);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return (await res.Content.ReadFromJsonAsync<Principal>(Ct))!;
    }

    private async Task<Created> CreateKeyAsync(Caller caller, Guid? principalId, params string[] scopes)
    {
        var res = await caller.Client.PostAsJsonAsync("/api/api-keys",
            new { name = "ci", scopes, servicePrincipalId = principalId }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return (await res.Content.ReadFromJsonAsync<Created>(Ct))!;
    }

    private async Task<HttpStatusCode> JobsWithAsync(string secret, string tenant)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/jobs");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        req.Headers.Add("X-Tenant", tenant);
        req.Headers.Add(TestRemoteIpFilter.Header, NextIp());
        return (await _factory.CreateClient().SendAsync(req, Ct)).StatusCode;
    }

    private async Task RemoveMembershipsAsync(Guid userId, string tenant)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.DeleteWhere<Membership>(m => m.UserId == userId && m.TenantSlug == tenant);
        await session.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_principal_key_survives_its_creator_losing_membership_and_a_creator_key_does_not()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);

        var principalKey = await CreateKeyAsync(creator, principal.Id, "cap:view_jobs");
        var ownKey = await CreateKeyAsync(creator, null, "cap:view_jobs");

        (await JobsWithAsync(principalKey.Key, tenant)).Should().Be(HttpStatusCode.OK);
        (await JobsWithAsync(ownKey.Key, tenant)).Should().Be(HttpStatusCode.OK);

        await RemoveMembershipsAsync(creator.UserId, tenant);

        (await JobsWithAsync(principalKey.Key, tenant)).Should().Be(HttpStatusCode.OK, "the principal, not the creator, is the identity");
        (await JobsWithAsync(ownKey.Key, tenant)).Should().Be(HttpStatusCode.Unauthorized, "a creator key stops with the creator's access");
    }

    [Fact]
    public async Task A_principal_key_without_the_cap_scope_is_refused_on_a_gated_endpoint()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);
        var key = await CreateKeyAsync(creator, principal.Id, "content:read");

        (await JobsWithAsync(key.Key, tenant)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_principal_key_cannot_use_a_capability_its_roles_lack()
    {
        var tenant = await TenantAsync();
        var other = await RoleAsync(tenant, [SystemCapabilities.ViewAuditLog]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewAuditLog);
        var principal = await CreatePrincipalAsync(creator, other.Id);
        var key = await CreateKeyAsync(creator, principal.Id, "cap:view_jobs");

        (await JobsWithAsync(key.Key, tenant)).Should().Be(HttpStatusCode.Forbidden, "the scope reaches the route, the roles still decide");
    }

    [Fact]
    public async Task A_disabled_principals_key_is_refused()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);
        var key = await CreateKeyAsync(creator, principal.Id, "cap:view_jobs");
        (await JobsWithAsync(key.Key, tenant)).Should().Be(HttpStatusCode.OK);

        var disable = await creator.Client.PostAsync($"/api/service-principals/{principal.Id}/disable", null, Ct);
        disable.StatusCode.Should().Be(HttpStatusCode.OK);

        (await JobsWithAsync(key.Key, tenant)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_key_cannot_be_made_for_a_principal_of_another_tenant()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var viewerB = await RoleAsync(tenantB, [SystemCapabilities.ViewJobs]);
        var stewardB = await StewardAsync(tenantB, SystemCapabilities.ViewJobs);
        var principalB = await CreatePrincipalAsync(stewardB, viewerB.Id);
        var stewardA = await StewardAsync(tenantA);

        var res = await stewardA.Client.PostAsJsonAsync("/api/api-keys",
            new { name = "x", scopes = new[] { "cap:view_jobs" }, servicePrincipalId = principalB.Id }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_platform_role_cannot_be_put_on_a_principal()
    {
        var tenant = await TenantAsync();
        var platform = await RoleAsync(tenant, [SystemCapabilities.ViewJobs], platform: true);
        var carrier = await RoleAsync(tenant, [SystemCapabilities.ManagePlatformRoles]);
        var creator = await StewardAsync(tenant);

        (await CreatePrincipalRawAsync(creator, platform.Id)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        (await CreatePrincipalRawAsync(creator, carrier.Id)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        (await CreatePrincipalRawAsync(creator, SystemRoles.SuperAdminRoleId)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_role_of_another_tenant_cannot_be_put_on_a_principal()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var foreign = await RoleAsync(tenantB, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenantA);

        (await CreatePrincipalRawAsync(creator, foreign.Id)).StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setting_roles_refuses_a_platform_role_and_keeps_the_old_roles()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var platform = await RoleAsync(tenant, [SystemCapabilities.ViewJobs], platform: true);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);

        var res = await creator.Client.PutAsJsonAsync($"/api/service-principals/{principal.Id}/roles",
            new { roleIds = new[] { platform.Id } }, Ct);

        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ServicePrincipal>(principal.Id, Ct);
        stored!.RoleIds.Should().Equal(viewer.Id);
    }

    [Fact]
    public async Task A_principal_cannot_hold_a_denied_cap_scope_at_creation()
    {
        var tenant = await TenantAsync();
        var creator = await StewardAsync(tenant);
        var principal = await CreatePrincipalAsync(creator);

        var res = await creator.Client.PostAsJsonAsync("/api/api-keys",
            new { name = "x", scopes = new[] { "cap:manage_api_keys" }, servicePrincipalId = principal.Id }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rotating_a_principal_key_keeps_the_principal()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);
        var key = await CreateKeyAsync(creator, principal.Id, "cap:view_jobs");

        var res = await creator.Client.PostAsJsonAsync($"/api/api-keys/{key.Id}/rotate", new { overlapMinutes = 0 }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        var fresh = (await res.Content.ReadFromJsonAsync<Created>(Ct))!;

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(fresh.Id, Ct);
        stored!.ServicePrincipalId.Should().Be(principal.Id);
    }

    [Fact]
    public async Task Moving_a_key_to_a_principal_is_opt_in_and_writes_an_audit_row()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var creator = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var principal = await CreatePrincipalAsync(creator, viewer.Id);
        var mine = await CreateKeyAsync(creator, null, "cap:view_jobs");
        var bystander = await StewardAsync(tenant);
        var theirs = await CreateKeyAsync(bystander, null, "cap:view_jobs");

        // Nothing moves until a caller asks.
        await StoredAsync(mine.Id, k => k.ServicePrincipalId.Should().BeNull());

        var res = await creator.Client.PostAsJsonAsync("/api/api-keys/move-to-principal",
            new { servicePrincipalId = principal.Id, allManageable = true }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        await StoredAsync(mine.Id, k => k.ServicePrincipalId.Should().Be(principal.Id));
        await StoredAsync(theirs.Id, k => k.ServicePrincipalId.Should().BeNull("a caller moves only the keys they may manage"));

        await RemoveMembershipsAsync(creator.UserId, tenant);
        (await JobsWithAsync(mine.Key, tenant)).Should().Be(HttpStatusCode.OK, "the moved key no longer depends on its creator");

        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<AuditEvent>().Where(a => a.Action == "apikey.moved_to_principal" && a.TargetId == principal.Id.ToString())
            .ToListAsync(Ct);
        audit.Should().HaveCount(1);
    }

    [Fact]
    public async Task Moving_a_single_key_that_is_not_the_callers_answers_404()
    {
        var tenant = await TenantAsync();
        var creator = await StewardAsync(tenant);
        var principal = await CreatePrincipalAsync(creator);
        var bystander = await StewardAsync(tenant);
        var theirs = await CreateKeyAsync(bystander, null, "content:read");

        var res = await creator.Client.PostAsJsonAsync("/api/api-keys/move-to-principal",
            new { servicePrincipalId = principal.Id, keyId = theirs.Id }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Principal_endpoints_need_the_manage_api_keys_capability()
    {
        var tenant = await TenantAsync();
        var role = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var userId = await MemberAsync(tenant, role.Id);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.CreateToken(
            roles: [role.Name], userId: userId.ToString(),
            additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());

        (await client.GetAsync("/api/service-principals", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/service-principals", new { name = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_caller_cannot_give_a_principal_a_capability_they_do_not_hold()
    {
        var tenant = await TenantAsync();
        var viewer = await RoleAsync(tenant, [SystemCapabilities.ViewJobs]);
        var holder = await StewardAsync(tenant, SystemCapabilities.ViewJobs);
        var lacking = await StewardAsync(tenant);
        var principal = await CreatePrincipalAsync(holder, viewer.Id);
        var empty = await CreatePrincipalAsync(holder);
        var own = await CreateKeyAsync(lacking, null, "content:read");

        (await CreatePrincipalRawAsync(lacking, viewer.Id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var setRoles = await lacking.Client.PutAsJsonAsync($"/api/service-principals/{empty.Id}/roles",
            new { roleIds = new[] { viewer.Id } }, Ct);
        setRoles.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var bind = await lacking.Client.PostAsJsonAsync("/api/api-keys",
            new { name = "x", scopes = new[] { "cap:view_jobs" }, servicePrincipalId = principal.Id }, Ct);
        bind.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var move = await lacking.Client.PostAsJsonAsync("/api/api-keys/move-to-principal",
            new { servicePrincipalId = principal.Id, keyId = own.Id }, Ct);
        move.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await StoredAsync(own.Id, k => k.ServicePrincipalId.Should().BeNull());

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ServicePrincipal>(empty.Id, Ct);
        stored!.RoleIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\u0007name")]
    [InlineData("bad\nname")]
    public async Task A_principal_name_that_is_blank_or_has_control_characters_is_refused(string name)
    {
        var tenant = await TenantAsync();
        var creator = await StewardAsync(tenant);

        var res = await creator.Client.PostAsJsonAsync("/api/service-principals", new { name }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_principal_name_is_trimmed_and_capped_at_100_characters()
    {
        var tenant = await TenantAsync();
        var creator = await StewardAsync(tenant);

        var tooLong = await creator.Client.PostAsJsonAsync("/api/service-principals", new { name = new string('n', 101) }, Ct);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await creator.Client.PostAsJsonAsync("/api/service-principals", new { name = "  ci runner  " }, Ct);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<Principal>(Ct))!.Name.Should().Be("ci runner");
    }

    [Fact]
    public async Task A_cap_scope_cannot_name_manage_stations_or_manage_config()
    {
        var tenant = await TenantAsync();
        var creator = await StewardAsync(tenant);

        foreach (var scope in new[] { "cap:manage_stations", "cap:manage_config" })
        {
            var res = await creator.Client.PostAsJsonAsync("/api/api-keys", new { name = "x", scopes = new[] { scope } }, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, scope);
        }
    }

    private async Task StoredAsync(Guid keyId, Action<ApiKey> check)
    {
        using var scope = _factory.Services.CreateScope();
        var key = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ApiKey>(keyId, Ct);
        check(key!);
    }
}
