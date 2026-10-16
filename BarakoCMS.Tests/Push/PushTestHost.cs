using System.Net.Http.Headers;
using BarakoCMS.Push;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Push;

public static class PushTestKeys
{
    public static string P256dh()
    {
        var b = new byte[65]; b[0] = 0x04; Random.Shared.NextBytes(b.AsSpan(1));
        return VapidKeyGenerator.Encode(b);
    }

    public static string Auth()
    {
        var b = new byte[16]; Random.Shared.NextBytes(b);
        return VapidKeyGenerator.Encode(b);
    }
}

public static class PushTestHost
{
    public static readonly string DefaultTenant = Tenant.DefaultSlug;

    private static readonly VapidKeys Keys = VapidKeyGenerator.Generate();
    private static readonly object Gate = new();
    private static readonly Dictionary<bool, WebApplicationFactory<Program>> Hosts = new();
    private static int _ip;

    public static string PublicKey => Keys.PublicKey;
    public static string PrivateKey => Keys.PrivateKey;

    public static string NewTenant() => $"push-{Guid.NewGuid():N}"[..14];

    // A null value removes the key, so the unconfigured host really has no VAPID settings.
    public static WebApplicationFactory<Program> HostFor(IntegrationTestFixture fx, bool configured)
    {
        lock (Gate)
        {
            if (Hosts.TryGetValue(configured, out var host)) return host;
            host = fx.WithSettings(new Dictionary<string, string?>
            {
                ["Modules:Push:PublicKey"] = configured ? Keys.PublicKey : null,
                ["Modules:Push:PrivateKey"] = configured ? Keys.PrivateKey : null,
                ["Modules:Push:Subject"] = configured ? "mailto:ops@example.org" : null,
            });
            Hosts[configured] = host;
            return host;
        }
    }

    public static async Task GrantSendPushAsync(IntegrationTestFixture fx)
    {
        var store = fx.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        await PushCapabilities.Defaults.GrantAsync(session, CancellationToken.None);
        await session.SaveChangesAsync();
    }

    public static IDocumentSession Open(IDocumentStore store, string tenant) =>
        tenant == DefaultTenant ? store.LightweightSession() : store.LightweightSession(tenant);

    public static IQuerySession OpenRead(IDocumentStore store, string tenant) =>
        tenant == DefaultTenant ? store.QuerySession() : store.QuerySession(tenant);

    public static Task<(HttpClient Client, Guid UserId)> SignedIn(
        IntegrationTestFixture fx, bool configured, string tenant = "default", params string[] roles) =>
        SignedInOn(HostFor(fx, configured), fx, tenant, roles);

    // Used by PushSenderHost, whose host has the fake transport swapped in.
    public static async Task<(HttpClient Client, Guid UserId)> SignedInOn(
        WebApplicationFactory<Program> host, IntegrationTestFixture fx, string tenant, params string[] roles)
    {
        var userId = await StoreUserAsync(fx, tenant, roles);
        var claims = new Dictionary<string, string> { ["Username"] = $"stored-{userId:n}" };

        var client = host.CreateClient();
        if (tenant != DefaultTenant)
        {
            claims["tenant"] = tenant;
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fx.CreateToken(roles, userId.ToString(), claims));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return (client, userId);
    }

    // An anonymous client on the same host.
    public static HttpClient Client(IntegrationTestFixture fx, bool configured)
    {
        var client = HostFor(fx, configured).CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }

    public static async Task<List<StoredSubscription>> RowsFor(
        IntegrationTestFixture fx, Guid userId, string tenant = "default")
    {
        var store = fx.Services.GetRequiredService<IDocumentStore>();
        await using var session = OpenRead(store, tenant);
        var rows = await session.Query<StoredSubscription>().Where(x => x.UserId == userId).ToListAsync();
        return rows.ToList();
    }

    private static string NextIp() => $"10.66.{Interlocked.Increment(ref _ip) / 250}.{Interlocked.Increment(ref _ip) % 250 + 1}";

    private static async Task<Guid> StoreUserAsync(IntegrationTestFixture fx, string tenant, string[] roleNames)
    {
        var store = fx.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();

        // A role name the seeder does not create (there is no "Member") is skipped, which leaves a
        // user with no capabilities. That is the right "wrong role" caller.
        var roleIds = new List<Guid>();
        foreach (var name in roleNames)
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is not null) roleIds.Add(role.Id);
        }

        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = $"stored-{id:n}",
            Email = $"stored-{id:n}@example.com",
            RoleIds = roleIds,
        });

        if (tenant != DefaultTenant)
        {
            if (await session.Query<Tenant>().FirstOrDefaultAsync(t => t.Slug == tenant) is null)
                session.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenant, Name = tenant });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = id,
                TenantSlug = tenant,
                Status = MembershipStatus.Active,
                RoleIds = roleIds,
            });
        }

        await session.SaveChangesAsync();
        return id;
    }
}
