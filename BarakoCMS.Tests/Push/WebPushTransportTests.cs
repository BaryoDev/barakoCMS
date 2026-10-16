using System.Net;
using System.Security.Cryptography;
using BarakoCMS.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Push;

public class WebPushTransportTests
{
    private static WebPushTransport Transport()
    {
        var k = VapidKeyGenerator.Generate();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicKey"] = k.PublicKey,
            ["PrivateKey"] = k.PrivateKey,
            ["Subject"] = "mailto:ops@example.org",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new PushModule().ConfigureServices(services, config);
        return services.BuildServiceProvider().GetRequiredService<WebPushTransport>();
    }

    private static StoredSubscription SubscriptionAt(string endpoint)
    {
        using var ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = ec.ExportParameters(false);
        var point = new byte[65];
        point[0] = 0x04;
        p.Q.X!.CopyTo(point, 1);
        p.Q.Y!.CopyTo(point, 33);
        return new StoredSubscription
        {
            Id = Guid.NewGuid(),
            Endpoint = endpoint,
            P256dh = VapidKeyGenerator.Encode(point),
            Auth = PushTestKeys.Auth(),
        };
    }

    private sealed class LocalPushService : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly List<string> _paths = new();
        public string Base { get; }
        public Func<string, (int Status, string? Location, string? RetryAfter)> Answer { get; set; } = _ => (201, null, null);

        public LocalPushService()
        {
            var port = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var n = ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            Base = $"http://127.0.0.1:{n}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext c;
                    try { c = await _listener.GetContextAsync(); } catch { return; }
                    var path = c.Request.Url!.AbsolutePath;
                    lock (_paths) _paths.Add(path);
                    var (status, location, retry) = Answer(path);
                    c.Response.StatusCode = status;
                    if (location is not null) c.Response.RedirectLocation = location;
                    if (retry is not null) c.Response.Headers["Retry-After"] = retry;
                    c.Response.Close();
                }
            });
        }

        public IReadOnlyList<string> Paths { get { lock (_paths) return _paths.ToList(); } }
        public void Dispose() => _listener.Close();
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_is_reported_as_the_status()
    {
        using var svc = new LocalPushService();
        svc.Answer = path => path == "/start" ? (302, svc.Base + "/internal", null) : (201, null, null);

        var status = await Transport().SendAsync(SubscriptionAt(svc.Base + "/start"), "{}", default);

        Assert.Equal(302, status);
        Assert.Equal(["/start"], svc.Paths);
    }

    [Fact]
    public async Task A_429_is_not_retried()
    {
        using var svc = new LocalPushService();
        svc.Answer = _ => (429, null, "1");

        var status = await Transport().SendAsync(SubscriptionAt(svc.Base + "/busy"), "{}", default);

        Assert.Equal(429, status);
        Assert.Single(svc.Paths);
    }
}
