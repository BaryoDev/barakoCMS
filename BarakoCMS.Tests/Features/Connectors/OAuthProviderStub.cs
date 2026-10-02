using System.Collections.Concurrent;
using System.Net;
using System.Text;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

internal sealed record ProviderCall(string Host, string Path, string? Authorization, string? Body);

/// <summary>
/// Stands in for the primary handler of the ExternalApi client, so a token request and the call it
/// is for are both answered in process and both recorded.
/// </summary>
/// <remarks>
/// Only what goes through the ExternalApi client reaches this. A token request sent through any
/// other client would dial a host that does not exist, and the tests counting token calls here
/// would count none.
///
/// The host this builds also swaps the logger factory for one that records every category at
/// Trace while a test has asked it to, so a test can read what the HTTP client's own loggers wrote.
/// </remarks>
internal sealed class OAuthProviderStub : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<ProviderCall>> Calls = new();
    private static readonly ConcurrentDictionary<string, Func<ProviderCall, CancellationToken, Task<HttpResponseMessage>>> Routes = new();

    private static readonly Lock HostLock = new();
    private static WebApplicationFactory<Program>? _host;

    internal static WebApplicationFactory<Program> HostFor(IntegrationTestFixture factory)
    {
        lock (HostLock)
        {
            return _host ??= factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                services.AddHttpClient("ExternalApi").ConfigurePrimaryHttpMessageHandler(() => new OAuthProviderStub());
                services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                    logging.SetMinimumLevel(LogLevel.Trace).AddProvider(new CapturedLogs())));
            }));
        }
    }

    internal static void Route(string host, Func<ProviderCall, CancellationToken, Task<HttpResponseMessage>> answer) =>
        Routes[host] = answer;

    internal static void Forget(string host)
    {
        Routes.TryRemove(host, out _);
        Calls.TryRemove(host, out _);
    }

    internal static List<ProviderCall> CallsTo(string host) =>
        Calls.TryGetValue(host, out var calls) ? calls.ToList() : [];

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = new ProviderCall(
            request.RequestUri!.IdnHost,
            request.RequestUri.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));

        if (!Routes.TryGetValue(call.Host, out var answer)) return new HttpResponseMessage(HttpStatusCode.NotFound);

        Calls.GetOrAdd(call.Host, _ => new ConcurrentQueue<ProviderCall>()).Enqueue(call);
        return await answer(call, ct);
    }
}

/// <summary>One pretend provider: a token endpoint on one host and an API on another.</summary>
internal sealed class FakeProvider
{
    internal const string ApiBody = "{\"ok\":true}";

    private readonly ConcurrentDictionary<string, bool> _live = new();
    private readonly TaskCompletionSource _apiReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _apiGate;
    private int _issued;

    internal FakeProvider(string slug)
    {
        Slug = slug;

        OAuthProviderStub.Route(AuthHost, (call, _) => Task.FromResult(TokenAnswer?.Invoke(call) ?? Issue()));

        OAuthProviderStub.Route(ApiHost, async (call, ct) =>
        {
            if (_apiGate is { } gate)
            {
                _apiReached.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }

            return !RefuseEverything
                && call.Authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
                && _live.ContainsKey(call.Authorization["Bearer ".Length..])
                    ? OAuthProviderStub.Json(HttpStatusCode.OK, ApiBody)
                    : OAuthProviderStub.Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\"}");
        });
    }

    internal string Slug { get; }
    internal string AuthHost => $"{Slug}-auth.example";
    internal string ApiHost => $"{Slug}-api.example";
    internal string TokenUrl => $"https://{AuthHost}/token";
    internal string BaseUrl => $"https://{ApiHost}";

    /// <summary>What goes after the token in the JSON, such as <c>,"expires_in":3600</c>.</summary>
    internal string Expiry { get; set; } = ",\"expires_in\":3600";

    /// <summary>Replaces the token endpoint's answer when it returns one.</summary>
    internal Func<ProviderCall, HttpResponseMessage?>? TokenAnswer { get; set; }

    internal bool RefuseEverything { get; set; }

    /// <summary>Completes when an API call has arrived and is being held by <see cref="HoldApi"/>.</summary>
    internal Task ApiReached => _apiReached.Task;

    internal string Token(int number) => $"at-{Slug}-{number}";

    internal void Revoke(int number) => _live.TryRemove(Token(number), out _);

    /// <summary>Makes the API keep every call waiting until <see cref="ReleaseApi"/>.</summary>
    internal void HoldApi() => _apiGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void ReleaseApi() => _apiGate?.TrySetResult();

    internal void Forget()
    {
        ReleaseApi();
        OAuthProviderStub.Forget(AuthHost);
        OAuthProviderStub.Forget(ApiHost);
    }

    internal List<ProviderCall> TokenCalls => OAuthProviderStub.CallsTo(AuthHost);
    internal List<ProviderCall> ApiCalls => OAuthProviderStub.CallsTo(ApiHost);

    internal ComposedRequest Post(string path = "/things") =>
        new("POST", BaseUrl + path, new(), "{\"n\":1}", "application/json");

    internal ComposedRequest Get(string path = "/things") =>
        new("GET", BaseUrl + path, new(), null, null);

    private HttpResponseMessage Issue()
    {
        var token = Token(Interlocked.Increment(ref _issued));
        _live[token] = true;

        return OAuthProviderStub.Json(HttpStatusCode.OK,
            $"{{\"access_token\":\"{token}\",\"token_type\":\"Bearer\"{Expiry}}}");
    }
}

/// <summary>A response body that never arrives: every read waits until it is cancelled.</summary>
internal sealed class StallingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class TestClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class CapturingLogger : ILogger<ConnectorSender>
{
    internal ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception) + exception);
}

/// <summary>
/// Every logger of the stub host, recorded only between <see cref="Start"/> and <see cref="Stop"/>.
/// </summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private static volatile ConcurrentQueue<string>? _lines;

    internal static ConcurrentQueue<string> Start()
    {
        var lines = new ConcurrentQueue<string>();
        _lines = lines;
        return lines;
    }

    internal static void Stop() => _lines = null;

    public ILogger CreateLogger(string categoryName) => new Recorder(categoryName);

    public void Dispose()
    {
    }

    private sealed class Recorder(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _lines?.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// What one test made: its providers, the connectors it seeded and the ones it created over HTTP.
/// Disposing it deletes the rows and forgets the stub's routes and recorded calls.
/// </summary>
internal sealed class OAuthTestScope(IntegrationTestFixture factory) : IAsyncDisposable
{
    private readonly List<FakeProvider> _providers = [];
    private readonly List<(string Tenant, Guid Id)> _seeded = [];
    private readonly List<string> _createdOverHttp = [];

    internal FakeProvider Provider(string? slug = null)
    {
        var provider = new FakeProvider(slug ?? OAuthConnectors.NewSlug());
        _providers.Add(provider);
        return provider;
    }

    /// <summary>Stores a client credentials connector and its secret straight into one tenant.</summary>
    internal async Task<Connector> SeedAsync(
        string tenant, FakeProvider provider, string clientSecret,
        Guid? id = null, DateTime? updatedAt = null, Dictionary<string, string>? settings = null)
    {
        var connector = new Connector
        {
            Id = id ?? Guid.NewGuid(),
            Name = "Client credentials",
            Slug = provider.Slug,
            BaseUrl = provider.BaseUrl,
            Auth = ConnectorAuth.OAuth2ClientCredentials,
            Settings = settings ?? new()
            {
                [ConnectorSettingKeys.TokenUrl] = provider.TokenUrl,
                [ConnectorSettingKeys.ClientId] = OAuthConnectors.ClientIdOf(provider),
            },
            SecretKeys = [ConnectorSecretKeys.ClientSecret],
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
        };

        var protector = factory.Services.GetRequiredService<IConnectorSecretProtector>();

        await using var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant);
        session.Store(connector);
        session.Store(new ConnectorSecret
        {
            Id = Guid.NewGuid(),
            ConnectorId = connector.Id,
            Key = ConnectorSecretKeys.ClientSecret,
            ProtectedValue = protector.Protect(clientSecret),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        _seeded.Add((tenant, connector.Id));
        return connector;
    }

    /// <summary>A connector made through the API lands in the tenant the fixture's requests reach.</summary>
    internal void CreatedOverHttp(string slug) => _createdOverHttp.Add(slug);

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in _seeded.GroupBy(s => s.Tenant))
        {
            await using var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession(tenant.Key);

            foreach (var (_, id) in tenant)
            {
                session.DeleteWhere<ConnectorSecret>(s => s.ConnectorId == id);
                session.Delete<Connector>(id);
            }

            await session.SaveChangesAsync();
        }

        if (_createdOverHttp.Count > 0)
        {
            using var scope = factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

            foreach (var slug in _createdOverHttp)
            {
                var connector = await session.Query<Connector>().FirstOrDefaultAsync(c => c.Slug == slug);
                if (connector is null) continue;

                var id = connector.Id;
                session.DeleteWhere<ConnectorSecret>(s => s.ConnectorId == id);
                session.Delete<Connector>(id);
            }

            await session.SaveChangesAsync();
        }

        foreach (var provider in _providers) provider.Forget();
    }
}

internal static class OAuthConnectors
{
    internal static string ClientIdOf(FakeProvider provider) => $"client-{provider.Slug}";

    /// <summary>The base64 half of the Basic header a token request for this provider carries.</summary>
    internal static string BasicPair(FakeProvider provider, string clientSecret) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientIdOf(provider)}:{clientSecret}"));

    internal static string BasicFor(FakeProvider provider, string clientSecret) =>
        "Basic " + BasicPair(provider, clientSecret);

    internal static ConnectorSender Sender(
        IServiceProvider services, IQuerySession session, ConnectorTokenCache cache,
        ILogger<ConnectorSender>? logger = null, TimeSpan? grantTimeout = null,
        IConnectorDeliveryLog? deliveries = null, TimeSpan? recordTimeout = null) =>
        new(
            services.GetRequiredService<IHttpClientFactory>(),
            session,
            services.GetRequiredService<IConnectorSecretProtector>(),
            cache,
            deliveries ?? services.GetRequiredService<IConnectorDeliveryLog>(),
            logger ?? new CapturingLogger())
        {
            GrantTimeout = grantTimeout ?? TimeSpan.FromSeconds(30),
            RecordTimeout = recordTimeout ?? TimeSpan.FromSeconds(5),
        };

    internal static string NewSlug() => "oauth" + Guid.NewGuid().ToString("n")[..10];

    internal static string NewTenant() => "oauth-" + Guid.NewGuid().ToString("n")[..8];
}
