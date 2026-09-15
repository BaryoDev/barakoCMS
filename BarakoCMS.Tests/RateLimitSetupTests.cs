using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using barakoCMS.Infrastructure.Security;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every rate limit is configuration, the defaults are the numbers that used to be hard coded, and a
/// configured renderer key gets its own global bucket so one barakoPress serving many sites does not
/// share one per-IP bucket (#823).
/// </summary>
/// <remarks>
/// The limiter tests run the real <see cref="RateLimitSetup.Configure"/> in a small in-memory host,
/// so a request past a limit costs microseconds and no database. <see cref="RateLimitWiringTests"/>
/// proves the full host reads the same section.
/// </remarks>
public class RateLimitSetupTests
{
    private const string Key = "renderer-key-for-tests-0123456789abcdef";
    private const string ClientIp = "203.0.113.20";
    private const string IpHeader = "X-Test-Ip";

    /// <summary>Later settings replace earlier ones with the same key.</summary>
    private static IConfiguration Config(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
            values[$"RateLimiting:{key}"] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<IHost> StartHost(IConfiguration configuration)
    {
        var settings = RateLimitSetup.Read(configuration);
        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddRateLimiter(o => RateLimitSetup.Configure(o, settings));
                });
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Connection.RemoteIpAddress = IPAddress.Parse(
                            ctx.Request.Headers.TryGetValue(IpHeader, out var ip) ? ip.ToString() : ClientIp);
                        await next();
                    });
                    app.UseRouting();
                    app.UseRateLimiter();
                    app.UseEndpoints(e =>
                    {
                        e.MapGet("/", () => "ok");
                        e.MapPost("/auth", () => "ok").RequireRateLimiting(RateLimitSetup.AuthPolicy);
                        e.MapPost("/batch", () => "ok").RequireRateLimiting(RateLimitSetup.BatchPolicy);
                        e.MapPost("/registration", () => "ok").RequireRateLimiting(RateLimitSetup.RegistrationPolicy);
                        e.MapPost("/site-share", () => "ok").RequireRateLimiting(RateLimitSetup.SiteSharePolicy);
                    });
                });
            })
            .StartAsync();
    }

    private static async Task<HttpStatusCode> Send(HttpClient client, string? key = null, string path = "/", string ip = ClientIp)
    {
        var request = new HttpRequestMessage(path == "/" ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Add(IpHeader, ip);
        if (key is not null)
            request.Headers.Add(RateLimitSetup.RendererHeader, key);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<List<HttpStatusCode>> SendMany(HttpClient client, int count, string? key = null, string path = "/", string ip = ClientIp)
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < count; i++)
            codes.Add(await Send(client, key, path, ip));
        return codes;
    }

    [Fact]
    public void The_defaults_are_the_limits_that_were_hard_coded()
    {
        var settings = RateLimitSetup.Read(Config());

        settings.Global.Should().Be(new RateLimitWindow(100, 60, 10));
        settings.Auth.Should().Be(new RateLimitWindow(5, 900, 0));
        settings.Batch.Should().Be(new RateLimitWindow(20, 60, 0));
        settings.Registration.Should().Be(new RateLimitWindow(5, 3600, 0));
        settings.SiteShare.Should().Be(new RateLimitWindow(10, 60, 0));
        settings.RendererKey.Should().BeNull("no key configured means no renderer partition");
    }

    [Fact]
    public void A_configured_value_replaces_only_its_own_default()
    {
        var settings = RateLimitSetup.Read(Config(("Global:PermitLimit", "600")));

        settings.Global.Should().Be(new RateLimitWindow(600, 60, 10));
        settings.Auth.Should().Be(RateLimitSetup.DefaultAuth);
    }

    [Theory]
    [InlineData("Global:PermitLimit", "0")]
    [InlineData("Global:PermitLimit", "-1")]
    [InlineData("Global:WindowSeconds", "0")]
    [InlineData("Global:QueueLimit", "-1")]
    [InlineData("Auth:PermitLimit", "0")]
    [InlineData("Auth:WindowSeconds", "-60")]
    [InlineData("Batch:PermitLimit", "0")]
    [InlineData("Registration:WindowSeconds", "0")]
    [InlineData("Renderer:PermitLimit", "0")]
    [InlineData("SiteShare:PermitLimit", "0")]
    [InlineData("Global:PermitLimit", "lots")]
    public void An_invalid_value_fails_at_startup_naming_the_setting(string key, string value)
    {
        var read = () => RateLimitSetup.Read(Config((key, value)));

        read.Should().Throw<InvalidOperationException>(
                "a zero or a typo must stop the host rather than remove or break the limit")
            .WithMessage($"*RateLimiting:{key}*");
    }

    [Fact]
    public void A_short_renderer_key_fails_at_startup_without_printing_the_key()
    {
        const string shortKey = "short-renderer-secret";

        var read = () => RateLimitSetup.Read(Config(("Renderer:Key", shortKey)));

        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*RateLimiting:Renderer:Key*")
            .Which.Message.Should().NotContain(shortKey);
    }

    [Fact]
    public void The_settings_never_print_the_renderer_key()
    {
        var settings = RateLimitSetup.Read(Config(("Renderer:Key", Key)));

        settings.RendererKey.Should().Be(Key);
        settings.ToString().Should().NotContain(Key, "a record's generated ToString would log the secret");
    }

    [Theory]
    [InlineData("Auth:PermitLimit", "6", "RateLimiting:Auth")]
    [InlineData("Auth:WindowSeconds", "60", "RateLimiting:Auth")]
    [InlineData("Registration:PermitLimit", "50", "RateLimiting:Registration")]
    public void Loosening_auth_or_registration_warns(string key, string value, string named)
    {
        var warnings = RateLimitSetup.Warnings(RateLimitSetup.Read(Config((key, value))));

        warnings.Should().HaveCount(1);
        warnings[0].Should().Contain(named);
    }

    [Fact]
    public void The_defaults_and_tighter_limits_do_not_warn()
    {
        RateLimitSetup.Warnings(RateLimitSetup.Read(Config())).Should().BeEmpty();
        RateLimitSetup.Warnings(RateLimitSetup.Read(Config(
            ("Auth:PermitLimit", "3"),
            ("Registration:PermitLimit", "2"),
            ("Global:PermitLimit", "5000")))).Should().BeEmpty(
            "tightening is always fine, and Global is not a guessing limit");
    }

    [Fact]
    public async Task The_101st_request_in_a_minute_is_refused_under_the_default_permit_limit()
    {
        // The default queue of 10 would hold the 101st request until the window ends, so it is
        // turned off here; the permit limit and window are the defaults.
        using var host = await StartHost(Config(("Global:QueueLimit", "0")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 100);
        codes.Should().HaveCount(100).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client)).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_configured_global_limit_applies()
    {
        using var host = await StartHost(Config(("Global:PermitLimit", "3"), ("Global:QueueLimit", "0")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 3);
        codes.Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client)).Should().Be(HttpStatusCode.TooManyRequests);
        (await Send(client, ip: "203.0.113.21")).Should().Be(HttpStatusCode.OK, "another IP has its own bucket");
    }

    private static IConfiguration WithRenderer(params (string Key, string Value)[] extra) => Config(
        new[]
        {
            ("Global:PermitLimit", "3"),
            ("Global:QueueLimit", "0"),
            ("Renderer:Key", Key),
            ("Renderer:PermitLimit", "10"),
            ("Renderer:QueueLimit", "0"),
        }.Concat(extra).ToArray());

    [Fact]
    public async Task The_renderer_key_is_counted_in_its_own_bucket_and_not_against_the_ip()
    {
        using var host = await StartHost(WithRenderer());
        var client = host.GetTestClient();

        var plain = await SendMany(client, 4);
        plain.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        var keyed = await SendMany(client, 8, key: Key);
        keyed.Should().HaveCount(8).And.OnlyContain(c => c == HttpStatusCode.OK,
            "this IP's bucket is spent, and the keyed requests are served from the renderer bucket");
    }

    [Fact]
    public async Task Keyed_requests_leave_the_ip_bucket_untouched()
    {
        using var host = await StartHost(WithRenderer());
        var client = host.GetTestClient();

        var keyed = await SendMany(client, 8, key: Key);
        keyed.Should().HaveCount(8).And.OnlyContain(c => c == HttpStatusCode.OK);

        var plain = await SendMany(client, 3);
        plain.Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.OK,
            "eight keyed requests from this IP spent nothing from its bucket of three");
    }

    [Fact]
    public async Task The_renderer_bucket_has_its_own_limit()
    {
        using var host = await StartHost(WithRenderer(("Renderer:PermitLimit", "2")));
        var client = host.GetTestClient();

        var keyed = await SendMany(client, 3, key: Key);
        keyed.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        (await Send(client)).Should().Be(HttpStatusCode.OK, "the IP bucket is separate and still has permits");
    }

    [Fact]
    public async Task A_wrong_key_is_counted_against_the_ip()
    {
        using var host = await StartHost(WithRenderer());
        var client = host.GetTestClient();

        var wrong = await SendMany(client, 3, key: Key + "x");
        wrong.Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client)).Should().Be(HttpStatusCode.TooManyRequests,
            "the three wrong-key requests spent this IP's bucket");
        (await Send(client, key: Key[..^1])).Should().Be(HttpStatusCode.TooManyRequests,
            "a prefix of the key is a wrong key");
    }

    [Fact]
    public async Task With_no_key_configured_the_header_is_ignored()
    {
        using var host = await StartHost(Config(("Global:PermitLimit", "3"), ("Global:QueueLimit", "0")));
        var client = host.GetTestClient();

        var codes = await SendMany(client, 3);
        codes.Should().HaveCount(3).And.OnlyContain(c => c == HttpStatusCode.OK);

        (await Send(client, key: Key)).Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData("/auth", "Auth")]
    [InlineData("/batch", "Batch")]
    [InlineData("/registration", "Registration")]
    public async Task The_endpoint_policies_still_limit_per_ip_with_a_valid_key(string path, string section)
    {
        using var host = await StartHost(WithRenderer(($"{section}:PermitLimit", "2")));
        var client = host.GetTestClient();

        var keyed = await SendMany(client, 3, key: Key, path: path);
        keyed.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode> Redeem(HttpClient client, string? key, string? visitor, string tenant = "site-a")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/site-share");
        request.Headers.Add(IpHeader, ClientIp);
        request.Headers.Add("X-Tenant", tenant);
        if (key is not null)
            request.Headers.Add(RateLimitSetup.RendererHeader, key);
        if (visitor is not null)
            request.Headers.TryAddWithoutValidation(RateLimitSetup.VisitorIpHeader, visitor);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static IConfiguration WithSiteShareLimitOfTwo() =>
        WithRenderer(("SiteShare:PermitLimit", "2"), ("Global:PermitLimit", "100"));

    [Fact]
    public async Task Two_visitors_behind_the_renderer_key_are_throttled_separately()
    {
        using var host = await StartHost(WithSiteShareLimitOfTwo());
        var client = host.GetTestClient();

        var first = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            first.Add(await Redeem(client, Key, "198.51.100.1"));
        first.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        (await Redeem(client, Key, "198.51.100.2")).Should().Be(HttpStatusCode.OK,
            "a second visitor behind the same renderer IP has its own bucket");
        (await Redeem(client, Key, "2001:db8::2")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_visitor_header_without_the_renderer_key_is_ignored()
    {
        using var host = await StartHost(WithSiteShareLimitOfTwo());
        var client = host.GetTestClient();

        (await Redeem(client, Key + "x", "198.51.100.1")).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, null, "198.51.100.2")).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, Key + "x", "198.51.100.3")).Should().Be(HttpStatusCode.TooManyRequests,
            "without the key every request is counted against the socket IP, whatever visitor it names");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("10.1")]
    public async Task A_visitor_header_that_is_not_a_full_ip_literal_is_ignored(string visitor)
    {
        using var host = await StartHost(WithSiteShareLimitOfTwo());
        var client = host.GetTestClient();

        (await Redeem(client, Key, null)).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, Key, null)).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, Key, visitor)).Should().Be(HttpStatusCode.TooManyRequests,
            "an unparseable visitor falls back to the socket IP, whose bucket is spent");
    }

    [Fact]
    public async Task The_site_share_bucket_is_per_tenant()
    {
        using var host = await StartHost(WithSiteShareLimitOfTwo());
        var client = host.GetTestClient();

        (await Redeem(client, null, null, "site-a")).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, null, null, "site-a")).Should().Be(HttpStatusCode.OK);
        (await Redeem(client, null, null, "site-a")).Should().Be(HttpStatusCode.TooManyRequests);
        (await Redeem(client, null, null, "site-b")).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_auth_policy_refuses_a_sixth_login_under_its_default_even_with_the_key()
    {
        using var host = await StartHost(WithRenderer());
        var client = host.GetTestClient();

        var keyed = await SendMany(client, 6, key: Key, path: "/auth");
        keyed.Should().Equal(
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK,
            HttpStatusCode.TooManyRequests);
    }
}

/// <summary>The full host builds its limiter from the <c>RateLimiting</c> section.</summary>
[Collection("Sequential")]
public class RateLimitWiringTests
{
    private const string Key = "renderer-key-for-wiring-0123456789abcdef";
    private const string Ip = "203.0.113.230";
    // A fixed branch that always answers 200 and sits behind the rate limiter. See CorsTests.
    private const string Route = "/health/build";

    private readonly IntegrationTestFixture _fixture;

    public RateLimitWiringTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_host_applies_the_configured_global_limit_and_the_renderer_partition()
    {
        var host = _fixture.WithSettings(new Dictionary<string, string?>
        {
            ["RateLimiting:Global:PermitLimit"] = "3",
            ["RateLimiting:Global:QueueLimit"] = "0",
            ["RateLimiting:Renderer:Key"] = Key,
        });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, Ip);
        var ct = TestContext.Current.CancellationToken;

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync(Route, ct);
            codes.Add(response.StatusCode);
        }
        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);

        using var keyed = new HttpRequestMessage(HttpMethod.Get, Route);
        keyed.Headers.Add(RateLimitSetup.RendererHeader, Key);
        using var keyedResponse = await client.SendAsync(keyed, ct);
        keyedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
