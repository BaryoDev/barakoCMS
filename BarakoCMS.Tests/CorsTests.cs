using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #522: <c>CORS:AllowedOrigins</c> (the <c>FRONTEND_ORIGINS</c> variable in production) is the
/// setting a frontend on another origin depends on, and nothing else asserted it. The host here lists
/// one origin; a preflight from it is answered with the allow headers and a preflight from any other
/// origin gets none.
/// </summary>
[Collection("Sequential")]
public class CorsTests
{
    private const string Listed = "https://console.example.com";
    private const string Unlisted = "https://evil.example.net";
    // /health/build, not /health/live: the liveness probe runs the private-memory check, which
    // answers 503 once the full suite has grown the test process past its threshold, and this
    // class asserts the allow header, not the health of the process. The build route is a fixed
    // branch that always answers 200.
    private const string PublicRoute = "/health/build";

    private readonly IntegrationTestFixture _factory;

    public CorsTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _host;

    private HttpClient ClientWithOneListedOrigin()
    {
        lock (Gate)
        {
            _host ??= _factory.WithSetting("CORS:AllowedOrigins", Listed);
        }

        return _host.CreateClient();
    }

    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, PublicRoute)
        {
            Headers =
            {
                { "Origin", origin },
                { "Access-Control-Request-Method", "GET" },
            },
        };

    [Fact]
    public async Task A_preflight_from_the_listed_origin_gets_the_allow_headers()
    {
        var client = ClientWithOneListedOrigin();

        var response = await client.SendAsync(Preflight(Listed), TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue(
            "a listed origin is answered with the allow headers, and without them the browser refuses the real request");
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(Listed);
        response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain(v => v.Contains("GET"));
        response.Headers.GetValues("Access-Control-Allow-Credentials").Should().ContainSingle().Which.Should().Be("true");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the CORS middleware answers the preflight itself");
    }

    [Fact]
    public async Task A_preflight_from_an_unlisted_origin_gets_no_allow_header()
    {
        var client = ClientWithOneListedOrigin();

        var response = await client.SendAsync(Preflight(Unlisted), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the CORS middleware still short-circuits the preflight, it just refuses to allow it");
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
        response.Headers.Contains("Access-Control-Allow-Credentials").Should().BeFalse();
    }

    [Fact]
    public async Task A_plain_request_from_the_listed_origin_carries_the_allow_header()
    {
        var client = ClientWithOneListedOrigin();
        var request = new HttpRequestMessage(HttpMethod.Get, PublicRoute);
        request.Headers.Add("Origin", Listed);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue(
            "a plain request from a listed origin carries the allow header, or the browser hides the response");
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle().Which.Should().Be(Listed);
    }

    [Fact]
    public async Task A_plain_request_from_an_unlisted_origin_carries_no_allow_header()
    {
        var client = ClientWithOneListedOrigin();
        var request = new HttpRequestMessage(HttpMethod.Get, PublicRoute);
        request.Headers.Add("Origin", Unlisted);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "CORS is enforced by the browser, the server still answers");
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    /// <summary>
    /// A browser can read the ETag the concurrency work emits (#680).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without <c>Access-Control-Expose-Headers</c> the only response headers script sees
    /// cross-origin are the seven CORS-safelisted ones, and <c>ETag</c> is not among them. #565 gave
    /// <c>Content</c> optimistic concurrency through <c>ETag</c> and <c>If-Match</c>, so a console
    /// that cannot read it cannot participate, and two editors overwrite each other silently.
    /// </para>
    /// <para>
    /// This asserts the header the browser obeys rather than the one the server emits. curl showed
    /// <c>ETag</c> the whole time, because curl does not enforce CORS, which is exactly why this went
    /// unnoticed. Nothing here can pass on the presence of <c>ETag</c> alone.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_listed_origin_may_read_the_etag_and_the_contract_version()
    {
        var client = ClientWithOneListedOrigin();
        var request = new HttpRequestMessage(HttpMethod.Get, PublicRoute);
        request.Headers.Add("Origin", Listed);

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Expose-Headers").Should().BeTrue(
            "without this header a browser hides every response header outside the safelisted seven");

        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers")
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        exposed.Should().NotBeEmpty("the assertions below run over this list");
        exposed.Should().Contain("ETag", "GET returns it and PUT takes it back as If-Match");
        exposed.Should().Contain(
            "X-Api-Contract-Version",
            "ApiContract documents this as what a caller reads to decide whether it can drive this API");
    }

    /// <summary>
    /// The same on the localhost fallback, which is the branch a developer and the quickstart run.
    /// </summary>
    /// <remarks>
    /// The two branches of SecurePolicy are separate builder chains, so a fix applied to one leaves
    /// the other wrong, and the one nobody tests is the one that behaves differently.
    /// </remarks>
    [Fact]
    public async Task The_localhost_fallback_exposes_them_too()
    {
        var host = _factory.WithSetting("CORS:AllowedOrigins", null);
        var request = new HttpRequestMessage(HttpMethod.Get, PublicRoute);
        request.Headers.Add("Origin", "http://localhost:3000");

        var response = await host.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("Access-Control-Allow-Origin").Should()
            .ContainSingle().Which.Should().Be("http://localhost:3000",
                "the positive control: without this the request was not on the fallback branch at all");

        var exposed = response.Headers.GetValues("Access-Control-Expose-Headers")
            .SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();

        exposed.Should().Contain("ETag");
        exposed.Should().Contain("X-Api-Contract-Version");
    }
}
