using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.Services;
using barakoCMS.Infrastructure.Auth;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Services;

/// <summary>
/// The registry is one deployment-wide table, so a heartbeat is bound to the service's own machine
/// key, or to a global holder of send_service_heartbeat when no key is registered.
/// </summary>
[Collection("Sequential")]
public class ServiceHeartbeatScopeTests
{
    private readonly IntegrationTestFixture _factory;

    public ServiceHeartbeatScopeTests(IntegrationTestFixture factory) => _factory = factory;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<ServiceRegistration?> LoadAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.Query<ServiceRegistration>().FirstOrDefaultAsync(s => s.Name == name, TestContext.Current.CancellationToken);
    }

    private static ServiceRegistration DownService(string name, Guid? machineKeyId = null) => new()
    {
        Name = name,
        MessageTypes = ["x"],
        DeliveryUrl = "https://x.example.com",
        MachineKeyId = machineKeyId,
        HeartbeatIntervalSeconds = 60,
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow.AddDays(-1),
        LastHeartbeatAt = DateTime.UtcNow.AddHours(-1),
    };

    private async Task<HttpClient> TenantAdminAsync()
    {
        var slug = Unique("hb");
        var userId = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug },
            new User { Id = userId, Username = username, Email = $"{Unique("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(
                roles: ["Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return client;
    }

    private async Task<HttpClient> GlobalAdminAsync() =>
        WithBearer(await _factory.StoredUserTokenAsync("Admin"));

    private HttpClient WithBearer(string bearer)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    /// <summary>A real stored key, owned by the global admin, authenticated through the API key handler.</summary>
    private async Task<(Guid Id, string Secret)> StoreKeyAsync(string[] scopes, bool revoked = false)
    {
        var token = await _factory.StoredUserTokenAsync("Admin");
        var ownerId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.First(c => c.Type == "UserId").Value);
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        var id = Guid.NewGuid();
        await StoreAsync(new ApiKey
        {
            Id = id,
            Name = "service",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = scopes.ToList(),
            Revoked = revoked,
        });
        return (id, secret);
    }

    private static Task<HttpResponseMessage> BeatAsync(HttpClient client, string name) =>
        client.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_tenant_only_holder_cannot_beat_a_service_and_nothing_changes()
    {
        var name = Unique("tenant-beat");
        var seeded = DownService(name);
        await StoreAsync(seeded);
        var caller = await TenantAdminAsync();

        var res = await caller.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var after = await LoadAsync(name);
        after.Should().NotBeNull();
        after!.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));
        after.UpdatedAt.Should().BeCloseTo(seeded.UpdatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_global_holder_can_beat_a_service_with_no_machine_key()
    {
        var name = Unique("global-beat");
        await StoreAsync(DownService(name));

        var res = await BeatAsync(await GlobalAdminAsync(), name);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_service_with_a_machine_key_hears_from_that_real_key()
    {
        var name = Unique("keyed-beat");
        var (keyId, secret) = await StoreKeyAsync(["services:heartbeat"]);
        await StoreAsync(DownService(name, keyId));

        var res = await BeatAsync(WithBearer(secret), name);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Another_real_key_or_a_global_role_cannot_beat_a_keyed_service_and_nothing_changes()
    {
        var name = Unique("keyed-other");
        var (keyId, _) = await StoreKeyAsync(["services:heartbeat"]);
        var (_, otherSecret) = await StoreKeyAsync(["services:heartbeat"]);
        var seeded = DownService(name, keyId);
        await StoreAsync(seeded);

        var other = await BeatAsync(WithBearer(otherSecret), name);
        var global = await BeatAsync(await GlobalAdminAsync(), name);
        var unknown = await BeatAsync(WithBearer(otherSecret), Unique("nope"));

        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        global.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(other.StatusCode, "an unknown name and a service that is not yours look the same");
        var after = (await LoadAsync(name))!;
        after.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));
        after.UpdatedAt.Should().BeCloseTo(seeded.UpdatedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_revoked_key_is_refused()
    {
        var name = Unique("keyed-revoked");
        var (keyId, secret) = await StoreKeyAsync(["services:heartbeat"], revoked: true);
        var seeded = DownService(name, keyId);
        await StoreAsync(seeded);

        var res = await BeatAsync(WithBearer(secret), name);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_key_without_the_heartbeat_scope_is_refused()
    {
        var name = Unique("keyed-noscope");
        var (keyId, secret) = await StoreKeyAsync(["*"]);
        var seeded = DownService(name, keyId);
        await StoreAsync(seeded);

        var res = await BeatAsync(WithBearer(secret), name);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the wildcard scope does not reach the heartbeat route");
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_key_cannot_reach_the_rest_of_the_registry()
    {
        var (_, secret) = await StoreKeyAsync(["services:heartbeat"]);

        var res = await WithBearer(secret).GetAsync("/api/services", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_key_owned_by_a_global_admin_cannot_beat_a_service_it_is_not_bound_to()
    {
        var unkeyed = Unique("unkeyed");
        var seeded = DownService(unkeyed);
        await StoreAsync(seeded);
        var (_, secret) = await StoreKeyAsync(["services:heartbeat"]);

        var res = await BeatAsync(WithBearer(secret), unkeyed);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound, "an API key counts for its own service only, not for its owner's global right");
        (await LoadAsync(unkeyed))!.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_heartbeat_key_is_refused_on_the_registry_routes_it_was_not_given()
    {
        var name = Unique("keyed-routes");
        var (keyId, secret) = await StoreKeyAsync(["services:heartbeat"]);
        await StoreAsync(DownService(name, keyId));
        var client = WithBearer(secret);

        (await client.PostAsJsonAsync("/api/services", new { name = Unique("new") }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/services/{name}/heartbeat", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BeatAsync(client, name)).StatusCode.Should().Be(HttpStatusCode.OK, "the same key does reach the heartbeat route");
    }

    [Fact]
    public async Task A_trailing_slash_and_other_casing_still_reach_the_bound_key()
    {
        var name = Unique("keyed-shape");
        var (keyId, secret) = await StoreKeyAsync(["services:heartbeat"]);
        await StoreAsync(DownService(name, keyId));
        var client = WithBearer(secret);

        (await client.PostAsync($"/api/services/{name}/heartbeat/", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/API/Services/{name.ToUpperInvariant()}/HEARTBEAT", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
