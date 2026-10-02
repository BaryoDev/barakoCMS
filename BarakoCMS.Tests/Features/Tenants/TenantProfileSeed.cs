using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>What the tenant profile tests set up: a tenant, a caller inside it, and its site entry.</summary>
/// <remarks>
/// Every tenant is made for one test and named at random, so nothing here depends on what another
/// class left in the shared database.
/// </remarks>
internal static class TenantProfileSeed
{
    /// <summary>Stores a tenant the way a release before the move left it, profile and all.</summary>
    public static async Task<string> TenantAsync(
        IntegrationTestFixture fixture, CancellationToken ct, Action<Tenant>? profile = null)
    {
        var slug = $"tp-{Guid.NewGuid():N}"[..16];
        var tenant = new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = $"Name of {slug}", IsActive = true };
        profile?.Invoke(tenant);

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(tenant);
        await session.SaveChangesAsync(ct);
        return slug;
    }

    public static async Task<Tenant> StoredAsync(IntegrationTestFixture fixture, string slug, CancellationToken ct)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<Tenant>().SingleAsync(t => t.Slug == slug, ct);
    }

    /// <summary>A SuperAdmin who is a member of the tenant, signed in to it.</summary>
    public static async Task<HttpClient> AdminInAsync(IntegrationTestFixture fixture, string slug, CancellationToken ct)
    {
        var userId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"tp-{Guid.NewGuid():n}"[..14],
                Email = $"tp-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = slug,
                Status = MembershipStatus.Active,
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(ct);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    public static HttpClient Anonymous(IntegrationTestFixture fixture, string? slug = null)
    {
        var client = fixture.CreateClient();
        if (slug is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    public static async Task ApplySiteBlueprintAsync(HttpClient admin, CancellationToken ct)
    {
        var applied = await admin.PostAsync("/api/content-types/blueprints/site", null, ct);
        applied.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", applied.StatusCode, await applied.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Creates the tenant's site entry as a draft and returns its id.</summary>
    public static async Task<Guid> CreateSiteEntryAsync(
        HttpClient admin, Dictionary<string, object> data, CancellationToken ct)
    {
        var created = await admin.PostAsJsonAsync("/api/contents", new { contentType = "site", data }, ct);
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync(ct));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    public static async Task PublishAsync(HttpClient admin, Guid id, CancellationToken ct)
    {
        var published = await admin.PutAsJsonAsync($"/api/contents/{id}/status", new { id, newStatus = "Published" }, ct);
        published.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", published.StatusCode, await published.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The anonymous profile route's answer for a tenant.</summary>
    public static async Task<JsonElement> PublicProfileAsync(IntegrationTestFixture fixture, string slug, CancellationToken ct)
    {
        var response = await Anonymous(fixture).GetAsync($"/api/tenants/{slug}/public", ct);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    /// <summary>A profile field of the answer, or null when the answer holds JSON null there.</summary>
    public static string? Field(JsonElement profile, string name)
    {
        profile.TryGetProperty(name, out var value).Should().BeTrue("the answer keeps its {0} field", name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static int _ip;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _ip);
        return $"10.88.{n / 250 % 250 + 1}.{n % 250 + 1}";
    }
}
