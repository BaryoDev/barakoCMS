using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The capability gate from issue #272: administrative endpoints ask for a capability the caller's
/// roles carry, rather than for a role name baked into C#.
/// </summary>
/// <remarks>
/// The fixture seeds SuperAdmin, Admin, HR and User with no capabilities at all, which is exactly the
/// state an existing deployment upgrades from. So the back-compat theory here runs against a real
/// pre-upgrade database rather than a contrived one.
/// </remarks>
[Collection("Sequential")]
public class CapabilityGateTests
{
    private readonly IntegrationTestFixture _factory;

    public CapabilityGateTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The headline of the issue. No code change, no seeded name: a role invented at runtime and
    /// given the capability reaches the endpoint.
    /// </summary>
    [Fact]
    public async Task A_role_created_at_runtime_with_the_capability_reaches_an_administrative_endpoint()
    {
        var (client, _) = await CallerHolding("Auditor", barakoCMS.Models.SystemCapabilities.ManageRoles);

        var response = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The other half of #272, and the #166 defect in general form. "Editor" is a name, not a grant.
    /// </summary>
    [Fact]
    public async Task A_role_gains_nothing_from_its_name_alone()
    {
        var (client, _) = await CallerHolding("Editor");

        var roles = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);
        var tenants = await client.GetAsync("/api/tenants", TestContext.Current.CancellationToken);

        roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        tenants.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A capability names one surface. Holding manage_tenants must not open the roles endpoints,
    /// otherwise the gate is a login check wearing a capability's name.
    /// </summary>
    [Fact]
    public async Task A_capability_opens_only_the_surface_it_names()
    {
        var (client, _) = await CallerHolding("Tenant Operator", barakoCMS.Models.SystemCapabilities.ManageTenants);

        var tenants = await client.GetAsync("/api/tenants", TestContext.Current.CancellationToken);
        var roles = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        tenants.StatusCode.Should().Be(HttpStatusCode.OK);
        roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Why this is a per-request lookup and not a claim in the token. The same token, unchanged and
    /// unexpired, stops working the moment the capability is taken off the role.
    /// </summary>
    [Fact]
    public async Task Revoking_a_capability_takes_effect_without_reissuing_the_token()
    {
        var (client, roleId) = await CallerHolding("Revocable Auditor", barakoCMS.Models.SystemCapabilities.ManageRoles);

        var before = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await SuperAdminClient();
        var revoke = await admin.PutAsJsonAsync(
            $"/api/roles/{roleId}",
            new { id = roleId, name = "Revocable Auditor", description = "", permissions = Array.Empty<object>(), systemCapabilities = Array.Empty<string>() },
            TestContext.Current.CancellationToken);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Back compatibility, isolated. This caller's stored user holds no roles at all, so the
    /// capability lookup cannot possibly grant anything; only the role name in the token can. That is
    /// the upgraded deployment whose SuperAdmin document has no capabilities yet.
    /// </summary>
    [Theory]
    [InlineData("SuperAdmin", "/api/roles")]
    [InlineData("SuperAdmin", "/api/tenants")]
    [InlineData("Admin", "/api/tenants/members")]
    public async Task A_seeded_role_name_still_opens_the_gate_it_used_to_open(string roleName, string path)
    {
        var client = await CallerWithNoStoredRoles(roleName);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "upgrading must not lock out a deployment whose stored roles predate capabilities");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Admin was never in the <c>Roles("SuperAdmin")</c> gate on roles and tenants, and must not
    /// acquire it here. Without this the back-compat fallback above could be written as "any admin
    /// role opens any admin endpoint" and still look green.
    /// </summary>
    [Fact]
    public async Task The_legacy_fallback_only_honours_the_names_the_endpoint_itself_gated_on()
    {
        var client = await CallerWithNoStoredRoles("Admin");

        var response = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Structural, in the spirit of <see cref="RoleGateTests"/>: a capability a core endpoint asks
    /// for must be one the vocabulary declares, so a typo cannot ship as an endpoint nobody can reach.
    /// </summary>
    [Fact]
    public void Every_capability_a_core_endpoint_requires_is_one_the_vocabulary_declares()
    {
        var core = typeof(Program).Assembly;

        var required = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly == core)
            .Select(endpoint => endpoint.Metadata.GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>())
            .Where(metadata => metadata is not null)
            .Select(metadata => metadata!.Capability)
            .Distinct()
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        required.Should().NotBeEmpty(
            "core endpoints declare capability gates, so reading none means this test stopped looking");
        required.Should().OnlyContain(capability => barakoCMS.Models.SystemCapabilities.IsKnown(capability));
    }

    /// <summary>
    /// A caller holding the seeded SuperAdmin role for real, so it is served by the capability path
    /// and not by the legacy name fallback. The revocation test uses it to make the change under
    /// test, and would otherwise be asserting two mechanisms at once.
    /// </summary>
    private async Task<HttpClient> SuperAdminClient()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"cap-super-{userId}",
            Email = $"cap-super-{userId}@example.com",
            RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: userId.ToString()));
        return client;
    }

    /// <summary>
    /// A signed-in caller whose one role is created here with the given capabilities. The role name
    /// is made unique per run: the fixture database is shared and role names are unique.
    /// </summary>
    private async Task<(HttpClient Client, Guid RoleId)> CallerHolding(string roleName, params string[] capabilities)
    {
        var unique = $"{roleName} {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new barakoCMS.Models.Role
        {
            Id = Guid.NewGuid(),
            Name = unique,
            SystemCapabilities = capabilities.ToList(),
        };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"cap-{userId}",
            Email = $"cap-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return (client, role.Id);
    }

    /// <summary>
    /// A caller whose token carries a role name but whose stored user holds no roles, so the
    /// capability lookup has nothing to resolve and only the legacy name can open a gate.
    /// </summary>
    private async Task<HttpClient> CallerWithNoStoredRoles(string tokenRoleName)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"legacy-{userId}",
            Email = $"legacy-{userId}@example.com",
            RoleIds = [],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [tokenRoleName], userId: userId.ToString()));
        return client;
    }
}
