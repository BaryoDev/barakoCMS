using BarakoCMS.Push;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.Tests.Push;

public sealed class PushSenderHost
{
    public static readonly FakePushTransport SharedFake = new();
    private static readonly object Gate = new();
    private static readonly Dictionary<bool, WebApplicationFactory<Program>> Hosts = new();

    private readonly IntegrationTestFixture _fx;
    private readonly WebApplicationFactory<Program> _host;

    private PushSenderHost(IntegrationTestFixture fx, WebApplicationFactory<Program> host, string tenant)
    {
        _fx = fx;
        _host = host;
        Tenant = tenant;
        Sender = new ScopedSender(host.Services);
    }

    public FakePushTransport Fake => SharedFake;
    public string Tenant { get; }
    public IPushSender Sender { get; }

    public static Task<PushSenderHost> Create(
        IntegrationTestFixture fx, Func<StoredSubscription, int> respond,
        string? tenant = null, bool configured = true)
    {
        SharedFake.Reset(respond);
        CapturingLoggerProvider.Clear();
        WebApplicationFactory<Program> host;
        lock (Gate)
        {
            if (!Hosts.TryGetValue(configured, out host!))
            {
                host = PushTestHost.HostFor(fx, configured).WithWebHostBuilder(b =>
                    b.ConfigureTestServices(s =>
                    {
                        s.AddSingleton<IPushTransport>(SharedFake);
                        // The host uses Serilog, which ignores providers added to the service collection.
                        s.AddSingleton<ILogger<PushSender>>(
                            new CapturingLoggerProvider.Typed<PushSender>(new CapturingLoggerProvider()));
                        // The test endpoints live on push.example, which the default allow list rejects.
                        s.AddSingleton(new PushOptions
                        {
                            PublicKey = configured ? PushTestHost.PublicKey : null,
                            PrivateKey = configured ? PushTestHost.PrivateKey : null,
                            Subject = configured ? "mailto:ops@example.org" : null,
                            AllowedHosts = [.. PushOptions.DefaultAllowedHosts, "push.example"],
                        });
                    }));
                Hosts[configured] = host;
            }
        }
        return Task.FromResult(new PushSenderHost(fx, host, tenant ?? PushTestHost.NewTenant()));
    }

    public async Task<Guid> Subscribe(string endpoint, Guid? userId = null)
    {
        var id = userId ?? Guid.NewGuid();
        var store = _fx.Services.GetRequiredService<IDocumentStore>();
        await using var session = PushTestHost.Open(store, Tenant);
        session.Store(new StoredSubscription
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Endpoint = endpoint,
            P256dh = PushTestKeys.P256dh(),
            Auth = PushTestKeys.Auth(),
            CreatedAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await session.SaveChangesAsync();
        return id;
    }

    public Task<List<StoredSubscription>> RowsFor(Guid userId) =>
        PushTestHost.RowsFor(_fx, userId, Tenant);

    public async Task<Guid> SubscriptionIdOf(Guid userId) => (await RowsFor(userId)).Single().Id;

    public async Task<(HttpClient Client, Guid UserId)> SignedInClient(params string[] roles) =>
        await PushTestHost.SignedInOn(_host, _fx, Tenant, roles);

    public HttpClient Anonymous()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"10.77.0.{Random.Shared.Next(1, 250)}");
        return client;
    }

    // The sender is scoped, so each call gets its own scope.
    private sealed class ScopedSender(IServiceProvider root) : IPushSender
    {
        public async Task<PushSendResult> SendToUsersAsync(string? tenant, IReadOnlyCollection<Guid> userIds,
            PushNotification n, CancellationToken ct)
        {
            using var scope = root.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPushSender>()
                .SendToUsersAsync(tenant, userIds, n, ct);
        }

        public async Task<PushSendResult> SendToTenantAsync(string? tenant, PushNotification n, CancellationToken ct)
        {
            using var scope = root.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPushSender>()
                .SendToTenantAsync(tenant, n, ct);
        }
    }
}
