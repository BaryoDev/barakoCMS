using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.Services;
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

    private async Task<HttpClient> GlobalAdminAsync(Guid? apiKeyId = null)
    {
        var stored = await _factory.StoredUserTokenAsync("Admin");
        var claims = new JwtSecurityTokenHandler().ReadJwtToken(stored).Claims.ToList();
        var userId = claims.First(c => c.Type == "UserId").Value;
        var username = claims.First(c => c.Type == "Username").Value;
        var extra = new Dictionary<string, string> { ["Username"] = username };
        if (apiKeyId is { } id) extra["apikey_id"] = id.ToString();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(roles: ["Admin"], userId: userId, additionalClaims: extra));
        return client;
    }

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
        var caller = await GlobalAdminAsync();

        var res = await caller.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_service_with_a_machine_key_only_hears_from_that_key()
    {
        var name = Unique("keyed-beat");
        var keyId = Guid.NewGuid();
        var seeded = DownService(name, keyId);
        await StoreAsync(seeded);

        var otherKey = await GlobalAdminAsync(Guid.NewGuid());
        (await otherKey.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var noKey = await GlobalAdminAsync();
        (await noKey.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "a global role is not the service's identity once a key is registered");
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeCloseTo(seeded.LastHeartbeatAt!.Value, TimeSpan.FromSeconds(1));

        var own = await GlobalAdminAsync(keyId);
        (await own.PostAsync($"/api/services/{name}/heartbeat", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadAsync(name))!.LastHeartbeatAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }
}
