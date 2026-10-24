using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Modules;

/// <summary>
/// GET /api/modules/{name}/configuration. The security cases carry their own secret beside a value
/// that must survive, so a route that returned nothing would fail as surely as one that leaked.
/// </summary>
[Collection("Integration1")]
public class ModuleConfigurationEndpointTests
{
    private const string Declared = "Probe Declared Module";
    private const string Silent = "Probe Silent Module";
    private const string Off = "Probe Off Module";

    // Fixture values, not real credentials.
    private const string Pass = "Zx9-fixture-pass";
    private const string Hidden = "internal-relay-9";
    private const string ShortPass = "hunter2";

    private readonly IntegrationTestFixture _fixture;
    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static readonly CapturedLogs Logs = new();

    public ModuleConfigurationEndpointTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Host()
    {
        lock (Gate)
        {
            return _host ??= _fixture.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"Modules:{Declared}:Host"] = "mail.example.com",
                    [$"Modules:{Declared}:Region"] = "eu-west",
                    [$"Modules:{Declared}:Password"] = Pass,
                    [$"Modules:{Declared}:Relay"] = Hidden,
                    [$"Modules:{Declared}:Smtp:Password"] = ShortPass,
                    [$"Modules:{Silent}:Host"] = "silent.example.com",
                    [$"Modules:{Off}:Host"] = "off.example.com",
                }));
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<ILoggerFactory>(_ => LoggerFactory.Create(logging =>
                        logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs)));
                    services.AddSingleton(new ModuleCatalogue(
                [
                    new ModuleCatalogueEntry(Declared, 1, Enabled: true)
                    {
                        ConfigurationView = new ModuleConfigurationView(
                        [
                            new("Host"),
                            new("Region", ConfigurationScope.Tenant),
                            new("Password"),
                            new("Smtp"),
                        ]),
                    },
                    new ModuleCatalogueEntry(Silent, 1, Enabled: true),
                    new ModuleCatalogueEntry(Off, 1, Enabled: false)
                    {
                        ConfigurationView = new ModuleConfigurationView([new("Host")]),
                    },
                ]));
                });
            });
        }
    }

    private async Task<HttpClient> SuperAdminAsync()
    {
        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task<HttpClient> CallerWithRolesAsync(params string[] roles)
    {
        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync(roles));
        return client;
    }

    private async Task<HttpClient> TenantAdminAsync()
    {
        var slug = $"mc-{Guid.NewGuid():N}"[..16];
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"r-{Guid.NewGuid():N}"[..18],
            TenantSlug = slug,
            SystemCapabilities = [SystemCapabilities.ViewModuleConfiguration],
        };
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
            session.Store(role);
            session.Store(new User { Id = userId, Username = $"mc{userId:N}"[..20], Email = $"mc{userId:N}@example.com" });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug, Status = MembershipStatus.Active, RoleIds = [role.Id],
            });
            await session.SaveChangesAsync(Ct);
        }

        _fixture.Services.GetRequiredService<ITenantDomainSource>().Invalidate();

        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreateToken(
            [role.Name], userId.ToString(), new Dictionary<string, string> { ["tenant"] = slug }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return client;
    }

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string module)
    {
        var response = await client.GetAsync($"/api/modules/{Uri.EscapeDataString(module)}/configuration", Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static List<JsonElement> Entries(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public async Task A_platform_administrator_reads_the_listed_keys_and_never_the_secrets()
    {
        var (response, body) = await GetAsync(await SuperAdminAsync(), Declared);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var entries = Entries(body);
        entries.Should().HaveCount(5, "Host, Region, Password, Smtp:Password under the listed Smtp, and the unlisted Relay");

        var byKey = entries.ToDictionary(e => e.GetProperty("key").GetString()!);
        byKey["Host"].GetProperty("value").GetString().Should().Be("mail.example.com");
        byKey["Region"].GetProperty("value").GetString().Should().Be("eu-west");
        byKey["Region"].GetProperty("scope").GetString().Should().Be("tenant");
        byKey["Password"].GetProperty("redacted").GetBoolean().Should().BeTrue();
        byKey["Relay"].GetProperty("redacted").GetBoolean().Should().BeTrue();
        byKey["Smtp:Password"].GetProperty("redacted").GetBoolean().Should().BeTrue();
        body.Should().NotContain(Pass).And.NotContain(Hidden).And.NotContain(ShortPass);
    }

    [Fact]
    public async Task A_module_that_declares_nothing_answers_200_with_an_empty_list()
    {
        var (response, body) = await GetAsync(await SuperAdminAsync(), Silent);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Entries(body).Should().BeEmpty();
        body.Should().NotContain("silent.example.com");
    }

    [Fact]
    public async Task An_unknown_module_answers_404_and_does_not_echo_the_name()
    {
        var (response, body) = await GetAsync(await SuperAdminAsync(), "Nope-Not-Here-123");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("Nope-Not-Here-123");
    }

    [Fact]
    public async Task A_disabled_module_answers_404()
    {
        var (response, body) = await GetAsync(await SuperAdminAsync(), Off);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        body.Should().NotContain("off.example.com");
    }

    [Fact]
    public async Task The_response_is_not_stored()
    {
        var (found, _) = await GetAsync(await SuperAdminAsync(), Declared);
        var (missing, _) = await GetAsync(await SuperAdminAsync(), "Nope-Not-Here-123");

        found.Headers.CacheControl!.NoStore.Should().BeTrue();
        missing.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task No_value_reaches_the_logs()
    {
        var (response, _) = await GetAsync(await SuperAdminAsync(), Declared);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var lines = Logs.Snapshot();
        lines.Should().NotBeEmpty("the host logs the request, so an empty capture means the provider is not attached");
        lines.Should().NotContain(l => l.Contains(Pass) || l.Contains(Hidden) || l.Contains("mail.example.com"));
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        var response = await Host().CreateClient().GetAsync($"/api/modules/{Declared}/configuration", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_the_capability_gets_403()
    {
        var (response, body) = await GetAsync(await CallerWithRolesAsync("User"), Declared);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        body.Should().NotContain("mail.example.com");
    }

    [Fact]
    public async Task A_tenant_administrator_with_the_capability_sees_tenant_scope_keys_only()
    {
        var (response, body) = await GetAsync(await TenantAdminAsync(), Declared);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var entries = Entries(body);
        entries.Should().HaveCount(1);
        entries[0].GetProperty("key").GetString().Should().Be("Region");
        body.Should().NotContain("mail.example.com").And.NotContain(Pass).And.NotContain(Hidden);
        body.Should().NotContain("\"Host\"").And.NotContain("\"Relay\"");
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public List<string> Snapshot()
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturedLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._lines)
                {
                    owner._lines.Add(formatter(state, exception) + exception);
                }
            }
        }
    }
}
