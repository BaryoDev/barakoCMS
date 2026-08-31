using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BarakoCMS.Tests.Features.Analytics;

/// <summary>
/// The Umami module exists so the analytics credentials stay on the server. This is that claim.
/// </summary>
/// <remarks>
/// Umami has no per-site read key. Reading stats means signing in as an account that can also
/// create and delete websites, so the credential the admin UI would need in order to talk to Umami
/// directly is a full one. The module's entire reason for existing is that the browser never gets
/// it: the server holds the account, exchanges it for a session token, and hands the page only the
/// numbers.
///
/// So there are two things to check, and neither can be seen from one side alone. What the browser
/// receives, which the HTTP response shows, and what the server sent to Umami, which only the
/// outbound handler shows. <see cref="UmamiStubHandler"/> records the second half.
/// </remarks>
[Collection("Sequential")]
public class UmamiProxyTests
{
    private const string Username = "analytics-reader";
    private const string Password = "NotARealUmamiPassword_9f2c1b";
    private const string SiteId = "site-1";

    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _configured;

    public UmamiProxyTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _configured = fixture.WithSettings(new Dictionary<string, string?>
        {
            { "Umami:Enabled", "true" },
            { "Umami:BaseUrl", "https://umami.test.example" },
            { "Umami:Username", Username },
            { "Umami:Password", Password },
        });
    }

    private HttpClient Admin(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(["Admin", "SuperAdmin"]));
        return client;
    }

    private HttpClient PlainUser()
    {
        var client = _configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(["User"]));
        return client;
    }

    private static readonly string[] Routes =
    [
        "/api/analytics/websites",
        $"/api/analytics/{SiteId}/summary",
        $"/api/analytics/{SiteId}/series",
        $"/api/analytics/{SiteId}/metric?type=url",
        $"/api/analytics/{SiteId}/status",
    ];

    /// <summary>
    /// The one test this module most needs: the numbers come back and the credential does not.
    /// </summary>
    [Fact]
    public async Task The_umami_account_never_reaches_the_browser()
    {
        UmamiStubHandler.Clear();
        var admin = Admin(_configured);

        var sites = await admin.GetAsync("/api/analytics/websites", TestContext.Current.CancellationToken);
        sites.StatusCode.Should().Be(HttpStatusCode.OK);
        var sitesBody = await sites.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var summary = await admin.GetAsync(
            $"/api/analytics/{SiteId}/summary", TestContext.Current.CancellationToken);
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        var summaryBody = await summary.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Status is the endpoint that builds a string out of configuration and hands it to the page,
        // which makes it the likeliest place for a setting to travel further than intended.
        var status = await admin.GetAsync(
            $"/api/analytics/{SiteId}/status", TestContext.Current.CancellationToken);
        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var statusBody = await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // The control. Without it every assertion below is satisfied by an endpoint that returns
        // nothing, which keeps the secret perfectly and is not what anyone wanted.
        sitesBody.Should().Contain("playground.example.com", "the proxy is meant to proxy");
        using var stats = JsonDocument.Parse(summaryBody);
        stats.RootElement.GetProperty("pageviews").GetProperty("value").GetInt64().Should().Be(120);
        using var installed = JsonDocument.Parse(statusBody);
        installed.RootElement.GetProperty("snippet").GetString()
            .Should().Contain("data-website-id=\"site-1\"");

        foreach (var body in new[] { sitesBody, summaryBody, statusBody })
        {
            body.Should().NotContain(Password, "the account password is the whole thing being kept back");
            body.Should().NotContain(Username, "the account name is half of the credential");
            body.Should().NotContain(UmamiStubHandler.IssuedToken,
                "the session token is a bearer credential for the same account, so handing it over "
                + "is handing over the account with an expiry attached");
        }

        var outbound = UmamiStubHandler.Requests;
        outbound.Should().NotBeEmpty("the server did have to talk to Umami for any of this to work");
        outbound.Where(r => r.Body.Contains(Password) || r.Body.Contains(Username))
            .Should().OnlyContain(r => r.Uri.EndsWith("api/auth/login", StringComparison.Ordinal),
                "the credential is spent once, on the login, and never travels with a data request");
        outbound.Where(r => !r.Uri.EndsWith("api/auth/login", StringComparison.Ordinal))
            .Should().OnlyContain(r => r.Authorization == $"Bearer {UmamiStubHandler.IssuedToken}",
                "data requests carry the exchanged token, which is what makes the exchange worth doing");
    }

    /// <summary>
    /// Every analytics route is admin-only. Site traffic is a business figure, and the create route
    /// writes into the analytics account.
    /// </summary>
    [Fact]
    public async Task Every_analytics_route_is_closed_to_a_caller_without_an_admin_role()
    {
        var user = PlainUser();

        foreach (var route in Routes)
        {
            (await user.GetAsync(route, TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden, "on {0}", route);
        }

        (await user.PostAsJsonAsync("/api/analytics/websites",
                new { name = "mine", domain = "mine.example.com" }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "creating a site writes into the shared analytics account");
    }

    /// <summary>
    /// And to an anonymous one, as unauthenticated rather than as not found.
    /// </summary>
    [Fact]
    public async Task Every_analytics_route_is_closed_to_an_anonymous_caller()
    {
        var anonymous = _configured.CreateClient();

        foreach (var route in Routes)
        {
            (await anonymous.GetAsync(route, TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "on {0}", route);
        }
    }

    /// <summary>
    /// With nothing configured the module reports itself off rather than reaching out and failing.
    /// </summary>
    /// <remarks>
    /// It ships turned off, so this is the state every install starts in. Making an outbound call
    /// here would turn a fresh install into a page that hangs on a connection to a host nobody set.
    /// </remarks>
    [Fact]
    public async Task An_unconfigured_module_says_so_and_calls_nobody()
    {
        UmamiStubHandler.Clear();

        var response = await Admin(_fixture).GetAsync(
            "/api/analytics/websites", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("configured").GetBoolean().Should().BeFalse();
        body.RootElement.GetProperty("websites").GetArrayLength().Should().Be(0);

        UmamiStubHandler.Requests.Should().BeEmpty(
            "there is nowhere to call, so calling anywhere is a bug rather than a timeout");
    }
}
