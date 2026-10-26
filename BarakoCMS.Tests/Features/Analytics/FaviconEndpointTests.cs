using System.Net;
using System.Net.Http.Headers;
using BarakoCMS.Analytics.Umami.Features;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Analytics;

/// <summary>
/// The favicon route's access rule and response headers (#677). The fetcher behind it is built over a
/// stub handler, so nothing here reaches the internet.
/// </summary>
[Collection("Integration1")]
public class FaviconEndpointTests
{
    private const string Route = "/api/analytics/favicon?domain=example.com";

    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _host;

    public FaviconEndpointTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton(new FaviconFetcher(new StubFactory()))));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> As(params string[] roles)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync(roles));
        return client;
    }

    [Fact]
    public async Task An_anonymous_caller_gets_401()
    {
        (await _host.CreateClient().GetAsync(Route, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_caller_without_an_admin_role_gets_403()
    {
        var user = await As("User");

        (await user.GetAsync(Route, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_allowed_caller_gets_the_image_with_a_fixed_type_and_nosniff()
    {
        var admin = await As("Admin", "SuperAdmin");

        var response = await admin.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle().Which.Should().Be("nosniff");
        response.Headers.CacheControl!.ToString().Should().Contain("max-age=86400");
        (await response.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_host_with_no_favicon_is_404_and_a_bad_domain_is_400()
    {
        var admin = await As("Admin", "SuperAdmin");

        (await admin.GetAsync("/api/analytics/favicon?domain=missing.example.com", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/analytics/favicon?domain=127.0.0.1", Ct))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Stub(), disposeHandler: true);
    }

    private sealed class Stub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "example.com")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
