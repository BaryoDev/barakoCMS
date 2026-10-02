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
/// </remarks>
internal sealed class OAuthProviderStub : HttpMessageHandler
{
    private static readonly ConcurrentQueue<ProviderCall> Calls = new();
    private static readonly ConcurrentDictionary<string, Func<ProviderCall, HttpResponseMessage>> Routes = new();

    private static readonly Lock HostLock = new();
    private static WebApplicationFactory<Program>? _host;

    internal static WebApplicationFactory<Program> HostFor(IntegrationTestFixture factory)
    {
        lock (HostLock)
        {
            return _host ??= factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
                services.AddHttpClient("ExternalApi").ConfigurePrimaryHttpMessageHandler(() => new OAuthProviderStub())));
        }
    }

    internal static void Route(string host, Func<ProviderCall, HttpResponseMessage> answer) => Routes[host] = answer;

    internal static List<ProviderCall> CallsTo(string host) => Calls.Where(c => c.Host == host).ToList();

    internal static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var call = new ProviderCall(
            request.RequestUri!.IdnHost,
            request.RequestUri.AbsolutePath,
            request.Headers.Authorization?.ToString(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));

        Calls.Enqueue(call);

        return Routes.TryGetValue(call.Host, out var answer)
            ? answer(call)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>One pretend provider: a token endpoint on one host and an API on another.</summary>
internal sealed class FakeProvider
{
    internal const string ApiBody = "{\"ok\":true}";

    private readonly ConcurrentDictionary<string, bool> _live = new();
    private int _issued;

    internal FakeProvider(string slug)
    {
        Slug = slug;

        OAuthProviderStub.Route(AuthHost, call => TokenAnswer?.Invoke(call) ?? Issue());

        OAuthProviderStub.Route(ApiHost, call =>
            !RefuseEverything
            && call.Authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true
            && _live.ContainsKey(call.Authorization["Bearer ".Length..])
                ? OAuthProviderStub.Json(HttpStatusCode.OK, ApiBody)
                : OAuthProviderStub.Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_token\"}"));
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

    internal string Token(int number) => $"at-{Slug}-{number}";

    internal void Revoke(int number) => _live.TryRemove(Token(number), out _);

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

/// <summary>Seeds a client credentials connector straight into one tenant, and builds a sender for it.</summary>
internal static class OAuthConnectors
{
    internal static async Task<Connector> SeedAsync(
        IServiceProvider services, string tenant, FakeProvider provider, string clientSecret,
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
                [ConnectorSettingKeys.ClientId] = ClientIdOf(provider),
            },
            SecretKeys = [ConnectorSecretKeys.ClientSecret],
            UpdatedAt = updatedAt ?? DateTime.UtcNow,
        };

        var store = services.GetRequiredService<IDocumentStore>();
        var protector = services.GetRequiredService<IConnectorSecretProtector>();

        await using var session = store.LightweightSession(tenant);
        session.Store(connector);
        session.Store(new ConnectorSecret
        {
            Id = Guid.NewGuid(),
            ConnectorId = connector.Id,
            Key = ConnectorSecretKeys.ClientSecret,
            ProtectedValue = protector.Protect(clientSecret),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return connector;
    }

    internal static string ClientIdOf(FakeProvider provider) => $"client-{provider.Slug}";

    internal static string BasicFor(FakeProvider provider, string clientSecret) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientIdOf(provider)}:{clientSecret}"));

    internal static ConnectorSender Sender(
        IServiceProvider services, IQuerySession session, ConnectorTokenCache cache, ILogger<ConnectorSender>? logger = null) =>
        new(
            services.GetRequiredService<IHttpClientFactory>(),
            session,
            services.GetRequiredService<IConnectorSecretProtector>(),
            cache,
            logger ?? new CapturingLogger());

    internal static string NewSlug() => "oauth" + Guid.NewGuid().ToString("n")[..10];

    internal static string NewTenant() => "oauth-" + Guid.NewGuid().ToString("n")[..8];
}
