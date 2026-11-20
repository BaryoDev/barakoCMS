using System.Collections.Concurrent;
using System.Net.Http.Headers;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Caching.Purge;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace BarakoCMS.Tests;

/// <summary>
/// One host for a purge test class, pointed at <see cref="CdnPurgeStandIn"/> through the outbound
/// allow list, and the arrange and read-back helpers the purge tests share (#561).
/// </summary>
/// <remarks>
/// The host is built once per class, because every derived host stays alive until another class
/// derives one. The sender never runs on its own here: the fixture sets a ten minute debounce, and
/// the tests call the sender with a clock past it. A test enables purging for its tenant after it
/// has arranged its entries, so what the arranging wrote is not in the pending rows it reads.
/// </remarks>
internal sealed class CdnPurgeTestHost
{
    public const string Token = "tok_PLACEHOLDER_purge_token_nobody_should_read_back";

    private static readonly ConcurrentDictionary<string, CdnPurgeTestHost> Hosts = new();
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    private CdnPurgeTestHost(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        StandIn = CdnPurgeStandIn.Shared;
        var settings = new Dictionary<string, string?>
        {
            ["Webhooks:AllowedHosts:0"] = $"127.0.0.1:{StandIn.Port}",
            ["Delivery:CdnPurge:BaseUrl:Cloudflare"] = StandIn.BaseUrl,
        };
        Factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingProvider(Logs)));
        });
    }

    public static CdnPurgeTestHost For(IntegrationTestFixture fixture, string testClass) =>
        Hosts.GetOrAdd(testClass, _ => new CdnPurgeTestHost(fixture));

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CdnPurgeStandIn StandIn { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public ConcurrentQueue<string> Logs { get; } = new();

    public IDocumentStore Store => Factory.Services.GetRequiredService<IDocumentStore>();

    public IConfigurationRoot Config => (IConfigurationRoot)Factory.Services.GetRequiredService<IConfiguration>();

    public static string NewZone() => $"zone{Guid.NewGuid():N}"[..20];

    public async Task<string> TenantAsync()
    {
        var slug = TestNames.Slug("pg");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    public async Task StoreAsync(params object[] documents)
    {
        await using var session = Store.LightweightSession();
        session.Store(documents);
        await session.SaveChangesAsync(Ct);
    }

    public async Task<string> DefineAsync(string tenant, string? type = null)
    {
        type ??= TestNames.Compact("pt");
        await using var session = Store.LightweightSession(TenantScopes.TenantIdFor(tenant));
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    /// <summary>Writes one entry through a tenant session, the way a writer that stores the document does.</summary>
    public async Task<Guid> EntryAsync(string tenant, string type, ContentStatus status = ContentStatus.Published, Guid? id = null)
    {
        var entry = id ?? Guid.NewGuid();
        await using var session = Store.LightweightSession(TenantScopes.TenantIdFor(tenant));
        var content = await session.LoadAsync<Content>(entry, Ct) ?? new Content { Id = entry, CreatedAt = DateTime.UtcNow };
        content.ContentType = type;
        content.Status = status;
        content.Data = new Dictionary<string, object> { ["Title"] = $"title {Guid.NewGuid():N}" };
        content.UpdatedAt = DateTime.UtcNow;
        session.Store(content);
        await session.SaveChangesAsync(Ct);
        return entry;
    }

    /// <summary>Turns purging on (or off) for a tenant with a stored, encrypted token.</summary>
    public async Task EnableAsync(string tenant, string zone, bool enabled = true)
    {
        var protector = Factory.Services.GetRequiredService<ISecretProtector>();
        await StoreAsync(new CdnPurgeSettings
        {
            Id = CdnPurgeSettings.IdForTenant(tenant),
            TenantSlug = tenant,
            Provider = CdnPurgeProvider.Cloudflare,
            Enabled = enabled,
            TargetId = zone,
            ProtectedToken = protector.Protect(Token),
        });
    }

    public CdnPurgeSender NewSender() => ActivatorUtilities.CreateInstance<CdnPurgeSender>(Factory.Services);

    /// <summary>Sends the tenant's due rows with a clock past the debounce window.</summary>
    public Task<int> SweepAsync(string tenant, CdnPurgeSender? sender = null, TimeSpan? ahead = null) =>
        (sender ?? Factory.Services.GetRequiredService<CdnPurgeSender>())
            .SweepTenantAsync(TenantScopes.TenantIdFor(tenant), DateTimeOffset.UtcNow + (ahead ?? TimeSpan.FromHours(1)), Ct);

    public async Task<IReadOnlyList<PendingCdnPurge>> PendingAsync(string tenant)
    {
        await using var query = Store.QuerySession(TenantScopes.TenantIdFor(tenant));
        return await query.Query<PendingCdnPurge>().ToListAsync(Ct);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> DeliveriesAsync(string tenant)
    {
        await using var query = Store.QuerySession(TenantScopes.TenantIdFor(tenant));
        return await query.Query<WebhookDelivery>().Where(d => d.Event == CdnPurgeSender.DeliveryEvent).ToListAsync(Ct);
    }

    public async Task<HttpClient> AdminClientAsync(string tenant)
    {
        var userId = Guid.NewGuid();
        var username = TestNames.Slug("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{TestNames.Slug("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = tenant,
                Status = MembershipStatus.Active, RoleIds = [SystemRoles.AdminRoleId],
            });

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _fixture.CreateToken(
                roles: ["Admin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenant, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.108.{Interlocked.Increment(ref _ipCounter) % 250 + 1}");
        return client;
    }

    public static string EntryTag(string tenant, Guid id) => DeliveryCache.Tag(tenant, CacheScope.Entry(id));

    public static string TypeTag(string tenant, string type) => DeliveryCache.Tag(tenant, CacheScope.Type(type));

    public static string SitemapTag(string tenant) => DeliveryCache.Tag(tenant, CacheScope.Named("sitemap"));

    private sealed class CapturingProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(lines, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> lines, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
