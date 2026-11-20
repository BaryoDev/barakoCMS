using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Caching.Purge;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A tenant admin can set the CDN purge provider and token for their own tenant, and the token is
/// never handed back (#561).
/// </summary>
[Collection("Integration1")]
public class CdnPurgeSettingsTests
{
    private const string Token = "tok_PLACEHOLDER_tenant_token_nobody_should_read_back";
    private const string Route = "/api/tenant-settings/cdn-purge";

    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public CdnPurgeSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NextIp() =>
        $"198.51.107.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    [Fact]
    public async Task A_tenant_admin_can_set_and_read_back_the_provider_and_the_token_is_never_returned()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync(Route,
            new { provider = "cloudflare", targetId = "zone123placeholder", token = Token, enabled = true }, Ct);
        var putBody = await put.Content.ReadAsStringAsync(Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK, putBody);
        putBody.Should().NotContain(Token);

        var read = await (await admin.GetAsync(Route, Ct)).Content.ReadAsStringAsync(Ct);
        read.Should().NotContain(Token);
        using var json = JsonDocument.Parse(read);
        json.RootElement.GetProperty("provider").GetString().Should().Be("cloudflare");
        json.RootElement.GetProperty("targetId").GetString().Should().Be("zone123placeholder");
        json.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("tokenSet").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_stored_token_is_encrypted_at_rest()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route, new { provider = "fastly", targetId = "svc123", token = Token }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<CdnPurgeSettings>(CdnPurgeSettings.IdForTenant(tenant), Ct);

        row.Should().NotBeNull();
        row!.TenantSlug.Should().Be(tenant);
        row.ProtectedToken.Should().NotBeEmpty().And.NotContain(Token);
        JsonSerializer.Serialize(row).Should().NotContain(Token);
        scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(row.ProtectedToken).Should().Be(Token);
    }

    [Fact]
    public async Task Enabling_without_a_token_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "fastly", targetId = "svc123", enabled = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<CdnPurgeSettings>(CdnPurgeSettings.IdForTenant(tenant), Ct))
            .Should().BeNull("a refused save writes nothing");
    }

    [Theory]
    [InlineData("zone/1")]
    [InlineData("zone.1")]
    [InlineData("../zone")]
    [InlineData("zone 1")]
    public async Task A_target_id_with_a_slash_or_a_dot_is_a_400(string target)
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "cloudflare", targetId = target }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("abc\ndef")]
    [InlineData("abc\r\nX-Injected: 1")]
    [InlineData("abc\n")]
    public async Task A_token_with_a_line_break_is_a_400(string token)
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "cloudflare", token }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_unknown_provider_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        (await admin.PutAsJsonAsync(Route, new { provider = "akamai" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_caller_without_manage_settings_is_403()
    {
        var tenant = await TenantAsync();
        var member = await MemberClientAsync(tenant, SystemRoles.UserRoleId, "User");

        (await member.PutAsJsonAsync(Route, new { provider = "cloudflare" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.GetAsync(Route, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_default_tenant_gets_a_400_and_reads_host_config()
    {
        var host = await HostAdminAsync();

        var put = await host.PutAsJsonAsync(Route, new { provider = "cloudflare" }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync(Ct)).Should().Contain("Delivery:CdnPurge");
        (await host.GetAsync(Route, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var config = new Dictionary<string, string?>
        {
            ["Delivery:CdnPurge:Enabled"] = "true",
            ["Delivery:CdnPurge:Provider"] = "fastly",
            ["Delivery:CdnPurge:TargetId"] = "svchost123",
            ["Delivery:CdnPurge:ApiToken"] = Token,
        };
        using var scope = _factory.Services.CreateScope();
        var target = await ResolverIn(scope, config).TargetForAsync(Tenant.DefaultSlug, Ct);
        target.Should().NotBeNull();
        target!.Provider.Should().Be("fastly");
        target.TargetId.Should().Be("svchost123");
        target.Token.Should().Be(Token);
        (await ResolverIn(scope, config).ForTenantAsync(Tenant.DefaultSlug, Ct)).Should().BeOfType<FastlyPurger>();

        config["Delivery:CdnPurge:Enabled"] = "false";
        (await ResolverIn(scope, config).ForTenantAsync(Tenant.DefaultSlug, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task One_tenants_settings_never_resolve_for_another_tenant()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var admin = await MemberClientAsync(tenantA, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route,
            new { provider = "cloudflare", targetId = "zonea123", token = Token, enabled = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var resolver = ResolverIn(scope, []);

        (await resolver.ForTenantAsync(tenantA, Ct)).Should().BeOfType<CloudflarePurger>();
        (await resolver.ForTenantAsync(tenantB, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Purge_is_off_for_a_tenant_that_never_saved_settings()
    {
        var tenant = await TenantAsync();

        using var scope = _factory.Services.CreateScope();
        (await ResolverIn(scope, []).ForTenantAsync(tenant, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Purge_is_off_for_a_tenant_that_saved_settings_but_did_not_enable_them()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route,
            new { provider = "cloudflare", targetId = "zone123", token = Token }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        (await ResolverIn(scope, []).ForTenantAsync(tenant, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_token_that_cannot_be_decrypted_resolves_to_no_purger_and_logs_without_the_value()
    {
        var tenant = await TenantAsync();
        var otherRing = new SecretProtector(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Secrets:Key"] = "some other key material that this deployment never had, 0123456789abcdef",
                })
                .Build());
        var sealedByAnotherKey = otherRing.Protect(Token);
        await StoreAsync(new CdnPurgeSettings
        {
            Id = CdnPurgeSettings.IdForTenant(tenant),
            TenantSlug = tenant,
            Provider = CdnPurgeProvider.Cloudflare,
            Enabled = true,
            TargetId = "zone123",
            ProtectedToken = sealedByAnotherKey,
        });

        var logs = new CapturingLoggerFactory();
        using var scope = _factory.Services.CreateScope();
        var purger = await ResolverIn(scope, [], logs).ForTenantAsync(tenant, Ct);

        purger.Should().BeNull();
        logs.Lines.Should().NotBeEmpty();
        logs.Lines.Should().Contain(l => l.Contains("cdn-purge") && l.Contains("Token"), "the section and key are named");
        logs.Lines.Should().OnlyContain(l => !l.Contains(Token) && !l.Contains(sealedByAnotherKey));
    }

    [Fact]
    public async Task A_tenant_cannot_change_the_host_the_purge_is_sent_to()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new
        {
            provider = "cloudflare", targetId = "zone123", token = Token, enabled = true,
            baseUrl = "https://evil.example", host = "evil.example",
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        using var scope = _factory.Services.CreateScope();
        var target = await ResolverIn(scope, []).TargetForAsync(tenant, Ct);
        target.Should().NotBeNull();
        target!.BaseUrl.Should().Be(CloudflarePurger.DefaultBaseUrl);

        var reached = new List<Uri>();
        var sent = await new CloudflarePurger(
                new HttpClient(new RecordingHandler(reached)), target, new LoggerFactory().CreateLogger<CloudflarePurger>())
            .PurgeTagsAsync(["t:acme:entry:1"], Ct);
        sent.Succeeded.Should().BeTrue();
        reached.Should().ContainSingle().Which.Host.Should().Be("api.cloudflare.com");

        var hostOverride = new Dictionary<string, string?> { ["Delivery:CdnPurge:BaseUrl:Cloudflare"] = "http://127.0.0.1:9/stand-in" };
        (await ResolverIn(scope, hostOverride).TargetForAsync(tenant, Ct))!.BaseUrl
            .Should().Be("http://127.0.0.1:9/stand-in", "only host configuration moves the host");
    }

    [Fact]
    public async Task Enabling_without_a_target_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "fastly", token = Token, enabled = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Enabling_without_a_provider_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { targetId = "svc123", token = Token, enabled = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_empty_target_id_clears_it()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route, new { provider = "fastly", targetId = "svc123", token = Token }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await admin.PutAsJsonAsync(Route, new { targetId = "" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = JsonDocument.Parse(await (await admin.GetAsync(Route, Ct)).Content.ReadAsStringAsync(Ct));
        read.RootElement.GetProperty("targetId").GetString().Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task An_empty_token_clears_it()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route, new { provider = "fastly", targetId = "svc123", token = Token }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await admin.PutAsJsonAsync(Route, new { token = "" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = JsonDocument.Parse(await (await admin.GetAsync(Route, Ct)).Content.ReadAsStringAsync(Ct));
        read.RootElement.GetProperty("tokenSet").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_put_with_only_enabled_leaves_provider_target_and_token_unchanged()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync(Route, new { provider = "fastly", targetId = "svc123", token = Token }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await admin.PutAsJsonAsync(Route, new { enabled = true }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        using var read = JsonDocument.Parse(await (await admin.GetAsync(Route, Ct)).Content.ReadAsStringAsync(Ct));
        read.RootElement.GetProperty("provider").GetString().Should().Be("fastly");
        read.RootElement.GetProperty("targetId").GetString().Should().Be("svc123");
        read.RootElement.GetProperty("tokenSet").GetBoolean().Should().BeTrue();
        read.RootElement.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_target_id_with_a_trailing_line_feed_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "cloudflare", targetId = "zone123\n" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_token_with_a_non_ascii_character_is_a_400()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync(Route, new { provider = "cloudflare", token = "tok\u00e9n" }, Ct);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(Ct)).Should().NotContain("tok\u00e9n", "the message names the field, not the value");
    }

    [Fact]
    public async Task The_resolver_from_the_host_builds_the_purger_for_the_tenant_provider()
    {
        var cloudflare = await TenantAsync();
        var fastly = await TenantAsync();
        var off = await TenantAsync();
        foreach (var (tenant, body) in new (string, object)[]
        {
            (cloudflare, new { provider = "cloudflare", targetId = "zone123", token = Token, enabled = true }),
            (fastly, new { provider = "fastly", targetId = "svc123", token = Token, enabled = true }),
            (off, new { provider = "fastly", targetId = "svc123", token = Token, enabled = false }),
        })
        {
            var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
            (await admin.PutAsJsonAsync(Route, body, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var scope = _factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<CdnPurgeResolver>();

        (await resolver.ForTenantAsync(cloudflare, Ct)).Should().BeOfType<CloudflarePurger>();
        (await resolver.ForTenantAsync(fastly, Ct)).Should().BeOfType<FastlyPurger>();
        (await resolver.ForTenantAsync(off, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task The_store_keeps_the_purge_settings_in_a_single_tenanted_table_under_its_alias()
    {
        using var scope = _factory.Services.CreateScope();
        await using var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(new CdnPurgeSettings { Id = CdnPurgeSettings.IdForTenant(TestNames.Slug("map")), TenantSlug = "map" });
        await session.SaveChangesAsync(Ct);

        var columns = new List<string>();
        await using var command = session.Connection!.CreateCommand();
        command.CommandText = "select column_name from information_schema.columns where table_name = 'mt_doc_cdn_purge_settings'";
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                columns.Add(reader.GetString(0));
            }
        }

        columns.Should().Contain("data", "the alias cdn_purge_settings names the table");
        columns.Should().NotContain("tenant_id", "the row is single tenanted, found by an id derived from the slug");
    }

    [Fact]
    public async Task The_change_is_audited_with_field_names_only()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync(Route,
            new { provider = "cloudflare", targetId = "zoneaudit123", token = Token, enabled = true }, Ct);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant && e.Action == "settings.cdn-purge.tenant.changed")
            .ToListAsync(Ct);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.TargetType.Should().Be(nameof(CdnPurgeSettings));
        entry.TargetId.Should().Be(CdnPurgeSettings.IdForTenant(tenant).ToString());
        var recorded = JsonSerializer.Serialize(entry);
        recorded.Should().Contain("token.set").And.Contain("provider").And.Contain("targetId").And.Contain("enabled");
        recorded.Should().NotContain(Token, "the audit row never carries the token");
        recorded.Should().NotContain("zoneaudit123", "values are not recorded, only field names");
        (await put.Content.ReadAsStringAsync(Ct)).Should().NotContain(Token);
    }

    [Fact]
    public async Task Two_concurrent_saves_of_different_fields_both_survive()
    {
        for (var round = 0; round < 5; round++)
        {
            var tenant = await TenantAsync();
            var one = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
            var two = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

            var results = await Task.WhenAll(
                one.PutAsJsonAsync(Route, new { provider = "fastly" }, Ct),
                two.PutAsJsonAsync(Route, new { targetId = "svc999" }, Ct));

            results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
            using var read = JsonDocument.Parse(await (await one.GetAsync(Route, Ct)).Content.ReadAsStringAsync(Ct));
            read.RootElement.GetProperty("provider").GetString().Should().Be("fastly");
            read.RootElement.GetProperty("targetId").GetString().Should().Be("svc999");
        }
    }

    private sealed class RecordingHandler(List<Uri> reached) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            reached.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        private sealed class Capture(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                    if (exception is not null)
                    {
                        lines.Add(exception.ToString());
                    }
                }
            }
        }
    }

    private static CdnPurgeResolver ResolverIn(IServiceScope scope, Dictionary<string, string?> config, ILoggerFactory? logs = null) =>
        new(scope.ServiceProvider.GetRequiredService<IQuerySession>(),
            scope.ServiceProvider.GetRequiredService<ISecretProtector>(),
            new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(),
            logs ?? scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

    private async Task<HttpClient> HostAdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents)
        {
            session.Store(document);
        }
        await session.SaveChangesAsync(Ct);
    }

    private async Task<string> TenantAsync()
    {
        var slug = TestNames.Slug("cdn");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    private async Task<HttpClient> MemberClientAsync(string slug, Guid roleId, string roleName)
    {
        var userId = Guid.NewGuid();
        var username = TestNames.Slug("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{TestNames.Slug("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [roleId],
            });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }
}
