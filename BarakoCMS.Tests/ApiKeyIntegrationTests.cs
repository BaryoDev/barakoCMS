using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// API keys end to end against the real API over a real Postgres. Beyond the happy path this is
/// deliberately adversarial: a forged, revoked, or expired key is refused, a read-only key can't
/// write, and a key can't reach the platform-management surface at all. Auth changes get abuse cases,
/// not just edge cases (see AI_DEVELOPMENT_LIFECYCLE.md).
/// </summary>
[Collection("Sequential")]
public class ApiKeyIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ApiKeyIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // A real SuperAdmin user + JWT. SuperAdmin so the content permission resolver bypasses (leaving the
    // API-key scope check as the thing under test), and a real user so a key can act as it.
    private async Task<(Guid userId, string token)> SuperAdminAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin")
                   ?? new Role { Id = barakoCMS.Data.DataSeeder.SuperAdminRoleId, Name = "SuperAdmin", Permissions = new() };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User { Id = userId, Username = $"admin-{userId}", Email = $"admin-{userId}@example.com", RoleIds = new() { role.Id } });
        await session.SaveChangesAsync();
        return (userId, _factory.CreateToken(new[] { "SuperAdmin" }, userId.ToString()));
    }

    // Insert a key with a known secret so a test can present it. tenant defaults to "default" so the
    // membership check is skipped — cross-tenant behaviour is its own concern.
    private async Task<string> StoreKeyAsync(Guid ownerId, string[] scopes, bool revoked = false, DateTime? expiresAt = null)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "test",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = scopes.ToList(),
            Revoked = revoked,
            ExpiresAt = expiresAt,
        });
        await session.SaveChangesAsync();
        return secret;
    }

    private HttpRequestMessage Get(string path, string bearer) => WithKey(HttpMethod.Get, path, bearer);
    private HttpRequestMessage WithKey(HttpMethod method, string path, string bearer)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    // ---- happy path ----------------------------------------------------------

    [Fact]
    public async Task ValidKey_WithContentRead_CanListContent()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var res = await _client.SendAsync(Get("/api/contents", secret));

        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());
    }

    // ---- abuse cases: bad credentials ---------------------------------------

    [Fact]
    public async Task ForgedKey_IsRefused()
    {
        var res = await _client.SendAsync(Get("/api/contents", "bcms_" + Guid.NewGuid().ToString("N")));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokedKey_IsRefused()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" }, revoked: true);

        var res = await _client.SendAsync(Get("/api/contents", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredKey_IsRefused()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" }, expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var res = await _client.SendAsync(Get("/api/contents", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- abuse cases: scope confinement -------------------------------------

    [Fact]
    public async Task ReadOnlyKey_CannotWriteContent()
    {
        var (userId, _) = await SuperAdminAsync();
        var secret = await StoreKeyAsync(userId, new[] { "content:read" });

        var req = WithKey(HttpMethod.Post, "/api/contents", secret);
        req.Content = JsonContent.Create(new { contentType = "anything", status = 1, sensitivity = 0, data = new Dictionary<string, object>() });
        var res = await _client.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a content:read key must not be allowed to write");
    }

    [Fact]
    public async Task Key_CannotReachManagementApi()
    {
        var (userId, _) = await SuperAdminAsync();
        // Even a wildcard key is confined to the content surface — it cannot manage users, tenants, or keys.
        var secret = await StoreKeyAsync(userId, new[] { "*" });

        var res = await _client.SendAsync(Get("/api/users", secret));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "API keys are limited to the content API");
    }

    // ---- management endpoints (human admin, JWT) ----------------------------

    [Fact]
    public async Task Admin_CanCreateListAndRevoke_AndSecretIsShownOnce()
    {
        var (_, token) = await SuperAdminAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // create
        var createRes = await _client.PostAsJsonAsync("/api/api-keys",
            new { name = "CI key", scopes = new[] { "content:read" } });
        createRes.StatusCode.Should().Be(HttpStatusCode.OK, because: await createRes.Content.ReadAsStringAsync());
        var created = await createRes.Content.ReadFromJsonAsync<CreatedKey>();
        created!.Key.Should().StartWith("bcms_", "the plaintext secret is returned once, on create");
        created.Id.Should().NotBe(Guid.Empty);

        // list — never leaks the secret or hash
        var listRes = await _client.GetAsync("/api/api-keys");
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await listRes.Content.ReadAsStringAsync();
        body.Should().Contain(created.Prefix);
        body.Should().NotContain(created.Key, "the full secret must never appear in a listing");
        body.ToLowerInvariant().Should().NotContain("keyhash");

        // revoke → the key stops working
        var secret = created.Key;
        var revokeRes = await _client.DeleteAsync($"/api/api-keys/{created.Id}");
        revokeRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        _client.DefaultRequestHeaders.Authorization = null;
        var useRes = await _client.SendAsync(Get("/api/contents", secret));
        useRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a revoked key is refused immediately");
    }

    [Fact]
    public async Task Create_RejectsUnknownScope()
    {
        var (_, token) = await SuperAdminAsync();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await _client.PostAsJsonAsync("/api/api-keys",
            new { name = "bad", scopes = new[] { "users:delete" } });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TouchingLastUsed_DoesNotResurrectARevokedKey()
    {
        // Guards the security-review finding: last-used must be a targeted patch, so a concurrent
        // revoke is never clobbered by a stale full-document write that resets Revoked to false.
        var keyId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new ApiKey
            {
                Id = keyId,
                Name = "race",
                KeyHash = ApiKeyService.Hash("bcms_" + Guid.NewGuid().ToString("N")),
                Prefix = "bcms_race",
                UserId = Guid.NewGuid(),
                TenantSlug = Tenant.DefaultSlug,
                Scopes = new() { "content:read" },
            });
            await s.SaveChangesAsync();
        }

        // An admin revokes it...
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var k = await s.LoadAsync<ApiKey>(keyId);
            k!.Revoked = true;
            s.Update(k);
            await s.SaveChangesAsync();
        }

        // ...then the same targeted patch the handler runs for last-used lands afterwards.
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Patch<ApiKey>(keyId).Set(x => x.LastUsedAt, DateTime.UtcNow);
            await s.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var k = await s.LoadAsync<ApiKey>(keyId);
            k!.Revoked.Should().BeTrue("a last-used patch must never un-revoke a key");
            k.LastUsedAt.Should().NotBeNull("the patch still records last-used");
        }
    }

    private sealed record CreatedKey(Guid Id, string Key, string Prefix, string Name);
}
