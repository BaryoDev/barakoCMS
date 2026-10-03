using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests.Features.Tenants;

/// <summary>
/// With <c>CORS:AllowTenantDomains</c> on, a page on a tenant's registered domain may call the API
/// from a browser, without credentials, as soon as the domain is saved (#904).
/// </summary>
/// <remarks>
/// Before this, a client's own domain was a data change in the API and then a config edit and a
/// restart before a browser on it could call anything. Each host here lists one origin in
/// <c>CORS:AllowedOrigins</c>, so every test also shows the configured list still answers as before.
/// </remarks>
[Collection("Sequential")]
public class TenantDomainCorsTests
{
    private const string Listed = "https://console.example.com";
    private const string Route = "/health/build";

    private readonly IntegrationTestFixture _fixture;

    public TenantDomainCorsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _on;
    private static WebApplicationFactory<Program>? _off;

    private WebApplicationFactory<Program> On()
    {
        lock (Gate)
        {
            return _on ??= _fixture.WithSettings(new Dictionary<string, string?>
            {
                ["CORS:AllowedOrigins"] = Listed,
                [TenantDomainCorsPolicyProvider.SettingKey] = "true",
            });
        }
    }

    private WebApplicationFactory<Program> Off()
    {
        lock (Gate)
        {
            return _off ??= _fixture.WithSetting("CORS:AllowedOrigins", Listed);
        }
    }

    private static string Handle() => $"cors-{Guid.NewGuid():n}"[..16];

    private static string Domain() => $"site-{Guid.NewGuid():n}"[..17] + ".example";

    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, Route)
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
            },
        };

    private static string? AllowedOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    private static bool VariesByOrigin(HttpResponseMessage response) =>
        response.Headers.Vary.Any(v => string.Equals(v, "Origin", StringComparison.OrdinalIgnoreCase));

    private async Task<HttpClient> SuperAdminOn(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    /// <summary>Stores a tenant directly and clears the named host's cached map.</summary>
    private async Task StoreTenantAsync(WebApplicationFactory<Program> host, string handle, string domain)
    {
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = handle, Name = handle, IsActive = true, Domains = [domain] });
            await session.SaveChangesAsync(Ct);
        }

        host.Services.GetRequiredService<ITenantDomainSource>().Invalidate();
    }

    [Fact]
    public async Task A_domain_added_to_a_tenant_passes_cors_on_the_next_request_without_a_restart()
    {
        var host = On();
        var admin = await SuperAdminOn(host);
        var client = host.CreateClient();
        var handle = Handle();
        var domain = Domain();
        var origin = $"https://{domain}";

        (await admin.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var before = await client.SendAsync(Preflight(origin), Ct);
        AllowedOrigin(before).Should().BeNull("the domain is not registered yet, and this also fills the cached map without it");

        var updated = await admin.PutAsJsonAsync($"/api/tenants/{handle}", new { name = handle, isActive = true, domains = new[] { domain } }, Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));

        var preflight = await client.SendAsync(Preflight(origin), Ct);
        AllowedOrigin(preflight).Should().Be(origin);
        preflight.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse(
            "a tenant domain never gets credentialed access, so it cannot ride a visitor's refresh cookie");
        VariesByOrigin(preflight).Should().BeTrue();

        var plain = new HttpRequestMessage(HttpMethod.Get, Route);
        plain.Headers.Add("Origin", origin);
        var response = await client.SendAsync(plain, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AllowedOrigin(response).Should().Be(origin);
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task A_domain_stops_passing_cors_once_its_tenant_is_switched_off()
    {
        var host = On();
        var admin = await SuperAdminOn(host);
        var client = host.CreateClient();
        var handle = Handle();
        var domain = Domain();
        var origin = $"https://{domain}";

        (await admin.PostAsJsonAsync("/api/tenants", new { handle, name = handle, isActive = true, domains = new[] { domain } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        AllowedOrigin(await client.SendAsync(Preflight(origin), Ct)).Should().Be(origin, "the positive control");

        (await admin.PutAsJsonAsync($"/api/tenants/{handle}", new { name = handle, isActive = false }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        AllowedOrigin(await client.SendAsync(Preflight(origin), Ct)).Should().BeNull(
            "the map holds active tenants only, and every tenant write clears it");
    }

    /// <summary>
    /// The positive controls come first: without them every refusal below would also pass on a host
    /// that allowed no tenant domain at all.
    /// </summary>
    [Fact]
    public async Task Only_the_https_origin_of_a_registered_domain_is_allowed()
    {
        var host = On();
        var client = host.CreateClient();
        var handle = Handle();
        var domain = Domain();
        await StoreTenantAsync(host, handle, domain);

        string[] allowed = [$"https://{domain}", $"https://www.{domain}", $"https://{domain.ToUpperInvariant()}"];
        foreach (var origin in allowed)
            AllowedOrigin(await client.SendAsync(Preflight(origin), Ct)).Should().Be(origin, "{0} is the registered site", origin);

        string[] refused =
        [
            $"http://{domain}",
            $"https://{domain}:8443",
            $"https://{domain}/",
            $"https://{domain}.",
            $"https://evil.{domain}",
            $"https://www.www.{domain}",
            $"https://{domain}.evil.example",
            $"https://evil-{domain}",
            $"https://user@{domain}",
            $"https://*.{domain}",
            "null",
            $"https://{handle}.example.com",
            "https://203.0.113.9",
        ];
        refused.Should().HaveCount(13);
        foreach (var origin in refused)
        {
            var response = await client.SendAsync(Preflight(origin), Ct);
            AllowedOrigin(response).Should().BeNull("{0} is not the registered site", origin);
            response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_listed_origin_keeps_credentials_and_every_answer_varies_by_origin()
    {
        var client = On().CreateClient();

        var listed = await client.SendAsync(Preflight(Listed), Ct);
        AllowedOrigin(listed).Should().Be(Listed);
        listed.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle().Which.Should().Be("true");
        VariesByOrigin(listed).Should().BeTrue(
            "a listed origin and a tenant domain now get different answers from one URL, so a shared cache must key on the origin");

        var unknown = await client.SendAsync(Preflight("https://unknown.example.net"), Ct);
        AllowedOrigin(unknown).Should().BeNull();
        VariesByOrigin(unknown).Should().BeTrue();
    }

    /// <summary>The default. A guard: it passes with or without the change, and says the default must not move.</summary>
    [Fact]
    public async Task With_the_setting_off_a_registered_domain_is_not_an_allowed_origin()
    {
        var host = Off();
        var client = host.CreateClient();
        var handle = Handle();
        var domain = Domain();
        await StoreTenantAsync(host, handle, domain);

        (await client.GetAsync($"/api/tenants/by-host/{domain}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK,
            "this host's map holds the domain, so the refusal below is the setting and not a stale cache");

        AllowedOrigin(await client.SendAsync(Preflight($"https://{domain}"), Ct)).Should().BeNull();

        var listed = await client.SendAsync(Preflight(Listed), Ct);
        AllowedOrigin(listed).Should().Be(Listed, "the configured list answers exactly as before");
        listed.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle().Which.Should().Be("true");
    }
}

/// <summary>The policy provider on its own: what it returns for each origin, the setting and a failing map.</summary>
public class TenantDomainCorsPolicyProviderTests
{
    private const string Listed = "https://console.example.com";
    private const string Mapped = "bakery.example";

    private sealed class FixedDomains(TenantDomainMap map) : ITenantDomainSource
    {
        public bool RefuseUnknownHosts => false;
        public Task<TenantDomainMap> GetAsync(CancellationToken ct = default) => Task.FromResult(map);
        public void Invalidate() { }
    }

    private sealed class FailingDomains : ITenantDomainSource
    {
        public bool RefuseUnknownHosts => false;
        public Task<TenantDomainMap> GetAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the database is down");
        public void Invalidate() { }
    }

    private static IConfiguration Setting(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [TenantDomainCorsPolicyProvider.SettingKey] = value })
        .Build();

    private static CorsOptions Configured()
    {
        var options = new CorsOptions();
        options.AddPolicy(TenantDomainCorsPolicyProvider.PolicyName, b => b
            .WithOrigins(Listed).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders("ETag").AllowCredentials());
        return options;
    }

    private static Task<CorsPolicy?> PolicyFor(string origin, ITenantDomainSource domains, string? setting = "true")
    {
        var provider = new TenantDomainCorsPolicyProvider(
            Options.Create(Configured()), domains, Setting(setting), NullLogger<TenantDomainCorsPolicyProvider>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Headers.Origin = origin;
        return provider.GetPolicyAsync(context, TenantDomainCorsPolicyProvider.PolicyName);
    }

    private static ITenantDomainSource WithMapped() =>
        new FixedDomains(new TenantDomainMap([(Mapped, "bakery")], ["bakery"]));

    [Fact]
    public async Task A_registered_domain_is_allowed_without_credentials_and_keeps_the_exposed_headers()
    {
        var policy = await PolicyFor($"https://{Mapped}", WithMapped());

        policy.Should().NotBeNull();
        policy!.IsOriginAllowed($"https://{Mapped}").Should().BeTrue();
        policy.IsOriginAllowed(Listed).Should().BeFalse("the answer is for the one origin that asked");
        policy.SupportsCredentials.Should().BeFalse();
        policy.ExposedHeaders.Should().ContainSingle().Which.Should().Be("ETag");
    }

    [Fact]
    public async Task A_listed_origin_gets_the_configured_policy_with_credentials()
    {
        var policy = await PolicyFor(Listed, WithMapped());

        policy!.IsOriginAllowed(Listed).Should().BeTrue();
        policy.SupportsCredentials.Should().BeTrue();
    }

    [Fact]
    public async Task When_the_domain_map_cannot_be_read_only_the_configured_origins_are_allowed()
    {
        var policy = await PolicyFor($"https://{Mapped}", new FailingDomains());

        policy!.IsOriginAllowed($"https://{Mapped}").Should().BeFalse("a failure closes, it never opens");
        policy.IsOriginAllowed(Listed).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("yes")]
    [InlineData("1")]
    public async Task Anything_but_true_leaves_the_setting_off(string? setting)
    {
        TenantDomainCorsPolicyProvider.IsOn(Setting(setting)).Should().BeFalse();

        var policy = await PolicyFor($"https://{Mapped}", WithMapped(), setting);
        policy!.IsOriginAllowed($"https://{Mapped}").Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void True_in_any_case_turns_it_on(string setting) =>
        TenantDomainCorsPolicyProvider.IsOn(Setting(setting)).Should().BeTrue();
}

/// <summary>Which values a tenant domain lookup accepts as a host.</summary>
public class TenantDomainLookupTests
{
    [Theory]
    [InlineData("https://bakery.example", "bakery.example")]
    [InlineData("https://www.bakery.example", "www.bakery.example")]
    [InlineData("HTTPS://Bakery.Example", "bakery.example")]
    public void An_https_origin_on_the_default_port_names_its_host(string origin, string host) =>
        TenantDomainLookup.OriginHost(origin).Should().Be(host);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("http://bakery.example")]
    [InlineData("https://bakery.example:443")]
    [InlineData("https://bakery.example/")]
    [InlineData("https://bakery.example/path")]
    [InlineData("https://bakery.example.")]
    [InlineData("https:// bakery.example")]
    [InlineData("https://user@bakery.example")]
    [InlineData("https://*.bakery.example")]
    [InlineData("https://203.0.113.9")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://localhost")]
    [InlineData("https://a.example,https://b.example")]
    [InlineData("bakery.example")]
    public void Anything_else_names_no_host(string? origin) =>
        TenantDomainLookup.OriginHost(origin).Should().BeNull();

    [Fact]
    public void A_name_longer_than_dns_allows_is_refused_before_it_is_parsed()
    {
        var longest = string.Join('.', Enumerable.Repeat(new string('a', 61), 4)) + ".examp";
        longest.Length.Should().Be(253);
        TenantDomainLookup.BareHost(longest).Should().Be(longest);

        TenantDomainLookup.BareHost("a" + longest).Should().BeNull();
        TenantDomainLookup.BareHost(new string('a', 10_000) + ".example").Should().BeNull();
    }
}
