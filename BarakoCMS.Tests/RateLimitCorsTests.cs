using System.Net;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A request the rate limiter refuses carries the CORS headers its origin is allowed, and a
/// <c>Retry-After</c> the policy lets a browser read (#1109).
/// </summary>
/// <remarks>
/// The limiter runs before the CORS middleware, so before this a refused request had no allow
/// header and a browser on another origin, the console, saw a network error instead of a 429. The
/// host here allows two requests a minute per address; each test uses an address of its own.
/// </remarks>
[Collection("Sequential")]
public class RateLimitCorsTests
{
    private const string Listed = "https://console.example.com";
    private const string Unlisted = "https://evil.example.net";
    private const string Route = "/health/build";
    private const int Allowed = 2;

    private readonly IntegrationTestFixture _fixture;

    public RateLimitCorsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;
    private static int _ip;

    private WebApplicationFactory<Program> Host()
    {
        lock (Gate)
        {
            return _host ??= _fixture.WithSettings(new Dictionary<string, string?>
            {
                ["CORS:AllowedOrigins"] = Listed,
                [TenantDomainCorsPolicyProvider.SettingKey] = "true",
                ["RateLimiting:Global:PermitLimit"] = Allowed.ToString(),
                ["RateLimiting:Global:WindowSeconds"] = "60",
                ["RateLimiting:Global:QueueLimit"] = "0",
            });
        }
    }

    private HttpClient FromNewAddress()
    {
        var client = Host().CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1109::{Interlocked.Increment(ref _ip):x}");
        return client;
    }

    private static HttpRequestMessage Get(string origin) =>
        new(HttpMethod.Get, Route) { Headers = { { "Origin", origin } } };

    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, Route)
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
            },
        };

    private static async Task<HttpResponseMessage> PastTheLimit(HttpClient client, string origin)
    {
        for (var i = 0; i < Allowed; i++)
        {
            using var allowed = await client.SendAsync(Get(origin), Ct);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK, "the first requests in the window are under the limit");
        }

        return await client.SendAsync(Get(origin), Ct);
    }

    private static string? AllowedOrigin(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;

    private static List<string> Exposed(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Access-Control-Expose-Headers", out var values)
            ? values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList()
            : [];

    [Fact]
    public async Task A_refused_request_from_a_listed_origin_carries_the_allow_header()
    {
        using var refused = await PastTheLimit(FromNewAddress(), Listed);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        AllowedOrigin(refused).Should().Be(Listed, "without it the browser reports a network error, not a 429");
        refused.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle().Which.Should().Be("true",
            "a listed origin keeps credentials on a refusal, as on any other answer");
    }

    [Fact]
    public async Task A_refused_request_says_when_to_retry_and_a_browser_may_read_it()
    {
        using var refused = await PastTheLimit(FromNewAddress(), Listed);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull("the limiter says how long to wait");
        refused.Headers.RetryAfter!.Delta.Should().NotBeNull();
        refused.Headers.RetryAfter.Delta!.Value.Should().Be(TimeSpan.FromSeconds(60),
            "a fixed window reports its whole window, which is never shorter than the time left in it");

        var exposed = Exposed(refused);
        exposed.Should().NotBeEmpty("the assertion below runs over this list");
        exposed.Should().Contain("Retry-After", "a browser hides a header the policy does not expose");
    }

    [Fact]
    public async Task A_listed_origin_may_read_retry_after_on_any_answer()
    {
        using var answered = await FromNewAddress().SendAsync(Get(Listed), Ct);

        answered.StatusCode.Should().Be(HttpStatusCode.OK);
        var exposed = Exposed(answered);
        exposed.Should().NotBeEmpty("the assertion below runs over this list");
        exposed.Should().Contain("Retry-After");
    }

    [Fact]
    public async Task A_refused_request_from_an_unlisted_origin_carries_no_cors_headers()
    {
        using var refused = await PastTheLimit(FromNewAddress(), Unlisted);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        refused.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
        refused.Headers.Contains("Access-Control-Expose-Headers").Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_request_from_a_tenant_domain_carries_the_allow_header_without_credentials()
    {
        var handle = $"rl-{Guid.NewGuid():n}"[..16];
        var domain = $"site-{Guid.NewGuid():n}"[..17] + ".example";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = handle, Name = handle, IsActive = true, Domains = [domain] });
            await session.SaveChangesAsync(Ct);
        }
        Host().Services.GetRequiredService<ITenantDomainSource>().Invalidate();
        var origin = $"https://{domain}";

        using var refused = await PastTheLimit(FromNewAddress(), origin);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        AllowedOrigin(refused).Should().Be(origin);
        refused.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse(
            "a tenant domain never rides a visitor's API session (#904), refused or not");
    }

    /// <summary>
    /// The limiter still runs before the CORS middleware, so a preflight is counted and cannot be
    /// used to get past it.
    /// </summary>
    [Fact]
    public async Task A_preflight_is_counted_by_the_limiter()
    {
        var client = FromNewAddress();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i <= Allowed; i++)
        {
            using var response = await client.SendAsync(Preflight(Listed), Ct);
            codes.Add(response.StatusCode);
        }

        codes.Should().HaveCount(Allowed + 1);
        codes.Take(Allowed).Should().OnlyContain(c => c == HttpStatusCode.NoContent);
        codes[Allowed].Should().Be(HttpStatusCode.TooManyRequests);
    }
}
