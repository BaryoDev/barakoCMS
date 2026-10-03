using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A rate limit is configuration: a policy under <c>RateLimiting:Policies</c> that a route names, an
/// opt-in limit on public delivery, and an opt-in quota per API key. With none of them set every
/// limit is the one it was (#888).
/// </summary>
/// <remarks>
/// These run the real <see cref="RateLimitSetup.Configure"/> and the real
/// <see cref="RateLimitAfterAuthentication"/> in a small in-memory host, where a header stands in
/// for the authentication handler. <see cref="DeliveryRateLimitWiringTests"/> and
/// <see cref="ApiKeyQuotaWiringTests"/> prove the full host is wired the same way.
/// </remarks>
public class NamedRateLimitPolicyTests
{
    private const string ClientIp = "203.0.113.40";
    private const string Proxy = "10.20.30.40";
    private const string IpHeader = "X-Test-Ip";
    private const string UserHeader = "X-Test-User-Id";
    private const string KeyHeader = "X-Test-Key-Id";
    private const string RendererKey = "renderer-key-for-named-0123456789abcdef";

    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
            values[$"RateLimiting:{key}"] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static readonly IConfiguration TrustingTheProxy = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ForwardedHeaders:Enabled"] = "true",
            ["ForwardedHeaders:KnownProxies:0"] = Proxy,
        })
        .Build();

    /// <summary>
    /// <c>/</c> names no policy, <c>/delivery</c> names the delivery policy, and each configured
    /// policy has a route at <c>/p/{name}</c> that names it.
    /// </summary>
    private static async Task<IHost> StartHost(IConfiguration configuration, IConfiguration? forwarded = null)
    {
        var settings = RateLimitSetup.Read(configuration);
        return await new HostBuilder()
            .ConfigureAppConfiguration((_, config) => config.AddConfiguration(configuration))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(o => RateLimitSetup.Configure(o, settings));
                    if (forwarded is not null)
                        services.Configure<ForwardedHeadersOptions>(o => ForwardedHeadersSetup.Configure(o, forwarded));
                });
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = IPAddress.Parse(
                            ctx.Request.Headers.TryGetValue(IpHeader, out var ip) ? ip.ToString() : ClientIp);
                        await next();
                    });
                    if (forwarded is not null)
                        app.UseForwardedHeaders();
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.Use(async (ctx, next) =>
                    {
                        var claims = new List<Claim>();
                        if (ctx.Request.Headers.TryGetValue(UserHeader, out var user))
                            claims.Add(new Claim(RateLimitAfterAuthentication.UserClaim, user.ToString()));
                        if (ctx.Request.Headers.TryGetValue(KeyHeader, out var key))
                            claims.Add(new Claim(RateLimitAfterAuthentication.ApiKeyClaim, key.ToString()));
                        if (claims.Count > 0)
                            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
                        await next();
                    });
                    app.UseMiddleware<RateLimitAfterAuthentication>();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/", () => "ok");
                        e.MapGet("/delivery", () => "ok").RequireRateLimiting(RateLimitSetup.DeliveryPolicy);
                        foreach (var policy in settings.Policies)
                            e.MapGet($"/p/{policy.Name}", () => "ok").RequireRateLimiting(policy.Name);
                    });
                });
            })
            .StartAsync();
    }

    private static async Task<HttpStatusCode> Send(
        HttpClient client,
        string path = "/",
        string ip = ClientIp,
        string? user = null,
        string? key = null,
        string? rendererKey = null,
        string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(IpHeader, ip);
        if (user is not null)
            request.Headers.Add(UserHeader, user);
        if (key is not null)
            request.Headers.Add(KeyHeader, key);
        if (rendererKey is not null)
            request.Headers.Add(RateLimitSetup.RendererHeader, rendererKey);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<List<HttpStatusCode>> SendMany(
        HttpClient client, int count, string path = "/", string ip = ClientIp, string? user = null, string? key = null, string? rendererKey = null)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
            codes.Add(await Send(client, path, ip, user, key, rendererKey));
        return codes;
    }

    private static readonly HttpStatusCode[] TwoThenRefused =
        [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests];

    [Fact]
    public void With_nothing_configured_every_limit_is_the_number_it_was_and_nothing_new_is_on()
    {
        var settings = RateLimitSetup.Read(Config());

        settings.Global.Should().Be(new RateLimitWindow(100, 60, 10));
        settings.Auth.Should().Be(new RateLimitWindow(5, 900, 0));
        settings.Batch.Should().Be(new RateLimitWindow(20, 60, 0));
        settings.Registration.Should().Be(new RateLimitWindow(5, 3600, 0));
        settings.Renderer.Should().Be(new RateLimitWindow(1000, 60, 10));
        settings.SiteShare.Should().Be(new RateLimitWindow(10, 60, 0));
        RateLimitSetup.Logout.Should().Be(new RateLimitWindow(30, 60, 0));
        RateLimitSetup.TlsAsk.Should().Be(new RateLimitWindow(60, 60, 0));

        settings.Delivery.Should().BeNull("delivery has no limit of its own until one is set");
        settings.ApiKey.Should().BeNull("an API key has no quota until one is set");
        settings.Policies.Should().BeEmpty();
    }

    [Fact]
    public void The_existing_sections_still_apply_beside_a_named_policy()
    {
        var settings = RateLimitSetup.Read(Config(
            ("Global:PermitLimit", "600"),
            ("Auth:PermitLimit", "3"),
            ("Policies:booking-lookup:PermitLimit", "20"),
            ("Policies:per-key:PermitLimit", "50"),
            ("Policies:per-key:WindowSeconds", "3600"),
            ("Policies:per-key:Partition", "apikey")));

        settings.Global.Should().Be(new RateLimitWindow(600, 60, 10));
        settings.Auth.Should().Be(new RateLimitWindow(3, 900, 0));
        settings.Registration.Should().Be(RateLimitSetup.DefaultRegistration);

        settings.Policies.Should().HaveCount(2);
        settings.Policies.Should().ContainSingle(p => p.Name == "booking-lookup")
            .Which.Should().Be(new NamedRateLimit("booking-lookup", new RateLimitWindow(20, 60, 0), RateLimitPartitionBy.Ip),
                "a policy counts per client IP over a minute unless it says otherwise");
        settings.Policies.Should().ContainSingle(p => p.Name == "per-key")
            .Which.Should().Be(new NamedRateLimit("per-key", new RateLimitWindow(50, 3600, 0), RateLimitPartitionBy.ApiKey));
    }

    [Theory]
    [InlineData("Policies:lookup:WindowSeconds", "30", "RateLimiting:Policies:lookup:PermitLimit")]
    [InlineData("Policies:lookup:Partition", "Ip", "RateLimiting:Policies:lookup:PermitLimit")]
    [InlineData("Policies:lookup:PermitLimit", "0", "RateLimiting:Policies:lookup:PermitLimit")]
    [InlineData("Policies:lookup:PermitLimit", "many", "RateLimiting:Policies:lookup:PermitLimit")]
    [InlineData("Policies:auth:PermitLimit", "50", "RateLimiting:Auth")]
    [InlineData("Policies:Telemetry:PermitLimit", "50", "RateLimiting:Batch")]
    [InlineData("Policies:delivery:PermitLimit", "50", "RateLimiting:Delivery")]
    [InlineData("Policies:logout:PermitLimit", "50", "RateLimiting:Policies:logout")]
    [InlineData("Policies:tls-ask:PermitLimit", "50", "RateLimiting:Policies:tls-ask")]
    [InlineData("Policies:two words:PermitLimit", "5", "RateLimiting:Policies")]
    [InlineData("Delivery:WindowSeconds", "30", "RateLimiting:Delivery:PermitLimit")]
    [InlineData("Delivery:PermitLimit", "0", "RateLimiting:Delivery:PermitLimit")]
    [InlineData("ApiKey:QueueLimit", "1", "RateLimiting:ApiKey:PermitLimit")]
    [InlineData("ApiKey:PermitLimit", "-5", "RateLimiting:ApiKey:PermitLimit")]
    public void A_policy_that_cannot_be_read_fails_at_startup_naming_the_setting(string key, string value, string named)
    {
        var read = () => RateLimitSetup.Read(Config((key, value)));

        read.Should().Throw<InvalidOperationException>(
                "a half-written or mistyped limit must stop the host, not run as no limit")
            .WithMessage($"*{named}*");
    }

    [Theory]
    [InlineData("Tenant")]
    [InlineData("7")]
    public void A_partition_that_is_not_ip_user_or_api_key_fails_at_startup(string partition)
    {
        var read = () => RateLimitSetup.Read(Config(
            ("Policies:lookup:PermitLimit", "5"),
            ("Policies:lookup:Partition", partition)));

        read.Should().Throw<InvalidOperationException>().WithMessage("*RateLimiting:Policies:lookup:Partition*");
    }

    [Fact]
    public async Task A_policy_defined_only_in_configuration_limits_the_route_that_names_it()
    {
        using var host = await StartHost(Config(("Policies:booking-lookup:PermitLimit", "2")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 3, path: "/p/booking-lookup");
        codes.Should().Equal(TwoThenRefused);

        (await Send(client)).Should().Be(HttpStatusCode.OK, "a route that names no policy is not counted by this one");
        (await Send(client, path: "/p/booking-lookup", ip: "203.0.113.41")).Should().Be(HttpStatusCode.OK,
            "another client IP has its own bucket");
    }

    [Fact]
    public async Task Two_policies_keep_separate_counts_for_one_client()
    {
        using var host = await StartHost(Config(
            ("Policies:lookup:PermitLimit", "2"),
            ("Policies:otp-send:PermitLimit", "1")));
        var client = host.GetTestClient();

        (await SendMany(client, 3, path: "/p/lookup")).Should().Equal(TwoThenRefused);
        (await SendMany(client, 2, path: "/p/otp-send")).Should().Equal(HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_forwarded_address_from_a_peer_nobody_trusts_does_not_pick_the_bucket()
    {
        using var host = await StartHost(Config(("Policies:lookup:PermitLimit", "2")), TrustingTheProxy);
        var client = host.GetTestClient();

        var forged = new List<HttpStatusCode>();
        for (var i = 1; i <= 3; i++)
            forged.Add(await Send(client, path: "/p/lookup", forwardedFor: $"198.51.100.{i}"));

        forged.Should().Equal(TwoThenRefused,
            "the peer is not the configured proxy, so each forged address is ignored and all three count against the peer");
    }

    [Fact]
    public async Task A_forwarded_address_from_the_configured_proxy_is_the_client()
    {
        using var host = await StartHost(Config(("Policies:lookup:PermitLimit", "2")), TrustingTheProxy);
        var client = host.GetTestClient();

        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            first.Add(await Send(client, path: "/p/lookup", ip: Proxy, forwardedFor: "198.51.100.1"));
        first.Should().Equal(TwoThenRefused);

        (await Send(client, path: "/p/lookup", ip: Proxy, forwardedFor: "198.51.100.2")).Should().Be(HttpStatusCode.OK,
            "a second client behind the trusted proxy has its own bucket");
    }

    [Fact]
    public async Task With_no_delivery_limit_a_delivery_route_is_only_under_the_global_limit()
    {
        using var host = await StartHost(Config(("Global:PermitLimit", "8"), ("Global:QueueLimit", "0")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 8, path: "/delivery");
        codes.Should().HaveCount(8).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client, path: "/delivery")).Should().Be(HttpStatusCode.TooManyRequests,
            "the ninth is refused by the global limit, as it is on any route");
    }

    [Fact]
    public async Task A_configured_delivery_limit_counts_per_client_ip_and_only_on_delivery()
    {
        using var host = await StartHost(Config(("Delivery:PermitLimit", "2")));
        var client = host.GetTestClient();

        (await SendMany(client, 3, path: "/delivery")).Should().Equal(TwoThenRefused);

        (await Send(client)).Should().Be(HttpStatusCode.OK, "other routes are not counted by the delivery limit");
        (await Send(client, path: "/delivery", ip: "203.0.113.41")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_renderer_is_not_counted_by_the_delivery_limit()
    {
        using var host = await StartHost(Config(
            ("Delivery:PermitLimit", "2"),
            ("Renderer:Key", RendererKey),
            ("Renderer:PermitLimit", "6"),
            ("Renderer:QueueLimit", "0")));
        var client = host.GetTestClient();

        var keyed = await SendMany(client, 6, path: "/delivery", rendererKey: RendererKey);
        keyed.Should().HaveCount(6).And.OnlyContain(c => c == HttpStatusCode.OK,
            "one renderer reads for every site from one IP, so it stays on the renderer bucket alone");

        (await Send(client, path: "/delivery", rendererKey: RendererKey)).Should().Be(HttpStatusCode.TooManyRequests,
            "the renderer bucket still has its own limit, unchanged");

        var wrong = await SendMany(client, 3, path: "/delivery", rendererKey: RendererKey + "x");
        wrong.Should().Equal(TwoThenRefused, "a wrong key is an ordinary client, counted by the delivery limit");
    }

    [Fact]
    public async Task The_api_key_quota_follows_the_key_whatever_address_it_comes_from()
    {
        using var host = await StartHost(Config(("ApiKey:PermitLimit", "2")));
        var client = host.GetTestClient();

        var codes = new List<HttpStatusCode>
        {
            await Send(client, ip: "203.0.113.50", key: "key-a"),
            await Send(client, ip: "203.0.113.51", key: "key-a"),
            await Send(client, ip: "203.0.113.52", key: "key-a"),
        };
        codes.Should().Equal(TwoThenRefused);

        (await Send(client, ip: "203.0.113.50", key: "key-b")).Should().Be(HttpStatusCode.OK,
            "one key spending its quota leaves another key's alone");
    }

    [Fact]
    public async Task The_api_key_quota_does_not_count_a_request_no_key_authenticated()
    {
        using var host = await StartHost(Config(("ApiKey:PermitLimit", "2")));
        var client = host.GetTestClient();

        var anonymous = await SendMany(client, 4);
        anonymous.Should().HaveCount(4).And.OnlyContain(c => c == HttpStatusCode.OK);

        var signedIn = await SendMany(client, 4, user: "user-a");
        signedIn.Should().HaveCount(4).And.OnlyContain(c => c == HttpStatusCode.OK,
            "a signed-in user without an API key is not on the key quota");
    }

    [Fact]
    public async Task With_no_api_key_quota_a_key_is_only_under_the_limits_it_already_had()
    {
        using var host = await StartHost(Config(("Global:PermitLimit", "8"), ("Global:QueueLimit", "0")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 8, key: "key-a");
        codes.Should().HaveCount(8).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client, key: "key-a")).Should().Be(HttpStatusCode.TooManyRequests, "the global limit per IP, as before");
    }

    [Fact]
    public async Task A_policy_partitioned_by_user_counts_each_user_and_puts_everyone_else_in_one_bucket()
    {
        using var host = await StartHost(Config(
            ("Policies:per-user:PermitLimit", "2"),
            ("Policies:per-user:Partition", "User")));
        var client = host.GetTestClient();

        var codes = new List<HttpStatusCode>
        {
            await Send(client, path: "/p/per-user", ip: "203.0.113.50", user: "user-a"),
            await Send(client, path: "/p/per-user", ip: "203.0.113.51", user: "user-a"),
            await Send(client, path: "/p/per-user", ip: "203.0.113.52", user: "user-a"),
        };
        codes.Should().Equal(TwoThenRefused, "the count follows the user across addresses");

        (await Send(client, path: "/p/per-user", user: "user-b")).Should().Be(HttpStatusCode.OK);

        var anonymous = new List<HttpStatusCode>
        {
            await Send(client, path: "/p/per-user", ip: "203.0.113.60"),
            await Send(client, path: "/p/per-user", ip: "203.0.113.61"),
            await Send(client, path: "/p/per-user", ip: "203.0.113.62"),
        };
        anonymous.Should().Equal(TwoThenRefused, "callers with no user share one bucket, they do not go uncounted");

        (await Send(client, user: "user-a")).Should().Be(HttpStatusCode.OK, "a route that names no policy is not counted");
    }

    [Fact]
    public async Task A_policy_partitioned_by_api_key_counts_each_key_and_puts_everyone_else_in_one_bucket()
    {
        using var host = await StartHost(Config(
            ("Policies:per-key:PermitLimit", "2"),
            ("Policies:per-key:Partition", "ApiKey")));
        var client = host.GetTestClient();

        (await SendMany(client, 3, path: "/p/per-key", user: "user-a", key: "key-a")).Should().Equal(TwoThenRefused);
        (await Send(client, path: "/p/per-key", user: "user-a", key: "key-b")).Should().Be(HttpStatusCode.OK,
            "a second key of the same user has its own bucket");

        var withoutKey = new List<HttpStatusCode>
        {
            await Send(client, path: "/p/per-key", user: "user-b"),
            await Send(client, path: "/p/per-key", user: "user-c"),
            await Send(client, path: "/p/per-key"),
        };
        withoutKey.Should().Equal(TwoThenRefused, "callers with no API key share one bucket");
    }

    private static RateLimiterOptions Registered(params (string Key, string Value)[] settings)
    {
        var options = new RateLimiterOptions();
        RateLimitSetup.Configure(options, RateLimitSetup.Read(Config(settings)));
        return options;
    }

    private static Endpoint Naming(string route, string policy) => new RouteEndpoint(
        _ => Task.CompletedTask,
        RoutePatternFactory.Parse(route),
        0,
        new EndpointMetadataCollection(new EnableRateLimitingAttribute(policy)),
        route);

    [Fact]
    public void A_route_naming_a_policy_nobody_defined_stops_the_host_naming_both()
    {
        var options = Registered(("Policies:booking-lookup:PermitLimit", "2"));
        Endpoint[] endpoints =
        [
            Naming("/api/bookings/lookup", "booking-lookup"),
            Naming("/api/bookings/status", "booking-status"),
        ];

        var check = () => RateLimitSetup.RequireRegisteredPolicies(endpoints, options);

        check.Should().Throw<InvalidOperationException>()
            .WithMessage("*'/api/bookings/status' names 'booking-status'*")
            .Which.Message.Should().NotContain("/api/bookings/lookup", "that route's policy exists");
    }

    [Fact]
    public void A_policy_name_that_differs_only_by_case_is_not_the_same_policy()
    {
        var options = Registered(("Policies:booking-lookup:PermitLimit", "2"));

        var check = () => RateLimitSetup.RequireRegisteredPolicies([Naming("/api/bookings/lookup", "Booking-Lookup")], options);

        check.Should().Throw<InvalidOperationException>("the framework matches policy names exactly")
            .WithMessage("*'Booking-Lookup'*");
    }

    [Fact]
    public void Routes_naming_built_in_configured_and_module_policies_pass_the_startup_check()
    {
        var options = Registered(("Policies:booking-lookup:PermitLimit", "2"));
        options.AddPolicy("forms", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("module"));
        Endpoint[] endpoints =
        [
            Naming("/api/auth/login", RateLimitSetup.AuthPolicy),
            Naming("/api/auth/logout", RateLimitSetup.LogoutPolicy),
            Naming("/api/tenants/tls-ask", RateLimitSetup.TlsAskPolicy),
            Naming("/api/client-errors", RateLimitSetup.BatchPolicy),
            Naming("/api/auth/register", RateLimitSetup.RegistrationPolicy),
            Naming("/api/public/site/share-links/redeem", RateLimitSetup.SiteSharePolicy),
            Naming("/api/public/{type}", RateLimitSetup.DeliveryPolicy),
            Naming("/api/bookings/lookup", "booking-lookup"),
            Naming("/api/public/forms/{slug}", "forms"),
            new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/health"), 0, EndpointMetadataCollection.Empty, "health"),
        ];

        var check = () => RateLimitSetup.RequireRegisteredPolicies(endpoints, options);

        check.Should().NotThrow("a policy a module registered in code counts as registered");
    }
}

/// <summary>The full host puts the delivery policy on the delivery routes.</summary>
[Collection("Sequential")]
public class DeliveryRateLimitWiringTests
{
    private readonly IntegrationTestFixture _fixture;

    public DeliveryRateLimitWiringTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static int _ip;

    private static HttpClient From(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:888::{Interlocked.Increment(ref _ip):x}");
        return client;
    }

    private static async Task<List<HttpStatusCode>> Get(HttpClient client, string path, int count)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            codes.Add(response.StatusCode);
        }
        return codes;
    }

    [Theory]
    [InlineData("/api/public/{0}")]
    [InlineData("/api/public/{0}/search?q=coffee")]
    [InlineData("/api/public/{0}/some-entry")]
    [InlineData("/api/public/{0}/feed.xml")]
    [InlineData("/api/public/sitemap.xml")]
    public async Task A_configured_delivery_limit_refuses_the_third_read_from_one_address(string route)
    {
        // One host for the class: every host a test builds stays alive for the rest of the run.
        _limited ??= _fixture.WithSettings(new Dictionary<string, string?> { ["RateLimiting:Delivery:PermitLimit"] = "2" });
        var path = string.Format(route, $"nosuchtype{Guid.NewGuid():N}");

        var codes = await Get(From(_limited), path, 3);

        codes.Should().HaveCount(3);
        codes[0].Should().NotBe(HttpStatusCode.TooManyRequests);
        codes[1].Should().NotBe(HttpStatusCode.TooManyRequests);
        codes[2].Should().Be(HttpStatusCode.TooManyRequests);

        var other = await Get(From(_limited), path, 1);
        other.Should().HaveCount(1);
        other[0].Should().Be(codes[0], "another address has its own bucket");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _limited;

    [Fact]
    public async Task With_no_delivery_limit_the_host_refuses_none_of_a_handful_of_reads()
    {
        var codes = await Get(From(_fixture), $"/api/public/nosuchtype{Guid.NewGuid():N}", 6);

        codes.Should().HaveCount(6).And.OnlyContain(c => c == HttpStatusCode.NotFound);
    }
}

/// <summary>The full host counts a request against the API key its handler verified.</summary>
[Collection("Sequential")]
public class ApiKeyQuotaWiringTests
{
    private readonly IntegrationTestFixture _fixture;

    public ApiKeyQuotaWiringTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static int _ip;

    private async Task<string> StoreKeyAsync(Guid ownerId)
    {
        var secret = "bcms_" + Guid.NewGuid().ToString("N");
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = "quota",
            KeyHash = ApiKeyService.Hash(secret),
            Prefix = secret[..12],
            UserId = ownerId,
            TenantSlug = Tenant.DefaultSlug,
            Scopes = ["content:read"],
        });
        await session.SaveChangesAsync();
        return secret;
    }

    private static async Task<HttpStatusCode> ListContents(HttpClient client, string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add(TestRemoteIpFilter.Header, $"2001:db8:889::{Interlocked.Increment(ref _ip):x}");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task A_configured_quota_refuses_the_third_request_of_one_key_and_no_other_caller()
    {
        var host = _fixture.WithSettings(new Dictionary<string, string?> { ["RateLimiting:ApiKey:PermitLimit"] = "2" });
        var client = host.CreateClient();
        var (token, userId) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var first = await StoreKeyAsync(userId);
        var second = await StoreKeyAsync(userId);

        var codes = new List<HttpStatusCode>
        {
            await ListContents(client, first),
            await ListContents(client, first),
            await ListContents(client, first),
        };
        codes.Should().Equal(
            new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests },
            "each request came from a different address, so only the key can be what was counted");

        (await ListContents(client, second)).Should().Be(HttpStatusCode.OK, "another key of the same user has its own quota");
        (await ListContents(client, token)).Should().Be(HttpStatusCode.OK, "a signed-in session is not on the key quota");
    }

    [Fact]
    public async Task With_no_quota_a_key_is_not_refused()
    {
        var client = _fixture.CreateClient();
        var (_, userId) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var key = await StoreKeyAsync(userId);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add(await ListContents(client, key));

        codes.Should().HaveCount(6).And.OnlyContain(c => c == HttpStatusCode.OK);
    }
}
