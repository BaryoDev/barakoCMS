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
    private const string PublicRoute = "/health/live";

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
}
