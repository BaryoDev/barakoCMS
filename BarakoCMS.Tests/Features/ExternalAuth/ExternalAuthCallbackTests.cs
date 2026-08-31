using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ExternalAuth;

/// <summary>Answers the GitHub OAuth endpoints with a scripted account instead of reaching GitHub.</summary>
internal sealed class GitHubStubHandler : HttpMessageHandler
{
    private readonly string _email;
    private readonly bool _verified;

    public GitHubStubHandler(string email, bool verified)
    {
        _email = email;
        _verified = verified;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri?.ToString() ?? "";
        var json = uri switch
        {
            var u when u.Contains("login/oauth/access_token", StringComparison.Ordinal)
                => """{"access_token":"gho_stub","token_type":"bearer","scope":"read:user user:email"}""",
            var u when u.EndsWith("api.github.com/user/emails", StringComparison.Ordinal)
                => $$"""[{"email":"{{_email}}","primary":true,"verified":{{(_verified ? "true" : "false")}} }]""",
            var u when u.EndsWith("api.github.com/user", StringComparison.Ordinal)
                => """{"login":"octo","name":"Octo Cat","avatar_url":"https://example.com/a.png","location":"Manila"}""",
            _ => "{}",
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return Task.FromResult(response);
    }
}

internal sealed class SingleClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// The OAuth callback is anonymous and reachable by anyone, and it is where a session token is
/// handed out. What it refuses is therefore the whole security of the provider flow.
/// </summary>
/// <remarks>
/// The <c>state</c> parameter is the only defence against a login CSRF: without it an attacker can
/// finish their own provider authorization in a victim's browser and leave the victim signed in as
/// the attacker, which quietly attributes everything the victim then writes to an account somebody
/// else controls.
///
/// GitHub is used as the representative provider because it is the one with a real choice to make
/// about the email, and because its four steps (state, token exchange, profile, verified address)
/// are the same four every other provider here performs.
///
/// Every client below is built with <c>HandleCookies = false</c>. It defaults to true, so a client
/// that has been through <c>/start</c> carries the state cookie into everything afterwards and a
/// test named "no cookie" quietly exercises the cookie path instead. The cookies are attached by
/// hand for the same reason: the ones the module sets are <c>Secure</c>, and the test host speaks
/// plain HTTP, so a cookie container would accept them and then never send them back.
/// </remarks>
[Collection("Sequential")]
public class ExternalAuthCallbackTests
{
    private const string BaseUrl = "https://cms.test.example";
    private const string ClientIp = "198.51.100.201";

    private readonly IntegrationTestFixture _fixture;

    public ExternalAuthCallbackTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private HttpClient Client(HttpMessageHandler? github = null)
    {
        var factory = _fixture
            .WithWebHostBuilder(b =>
            {
                b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        { "App:BaseUrl", BaseUrl },
                        { "GitHub:ClientId", "test-client-id" },
                        { "GitHub:ClientSecret", "test-client-secret" },
                        { "Google:ClientId", "" },
                        { "Facebook:AppId", "" },
                        { "LinkedIn:ClientId", "" },
                    }));

                if (github is not null)
                {
                    b.ConfigureServices(s => s.AddSingleton<IHttpClientFactory>(
                        new SingleClientFactory(github)));
                }
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ClientIp);
        return client;
    }

    private static string StateCookieFrom(HttpResponseMessage start)
    {
        var cookie = start.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("gh_state=", StringComparison.Ordinal));
        return cookie.Split(';')[0]["gh_state=".Length..];
    }

    private static async Task<HttpResponseMessage> CallbackAsync(
        HttpClient client, string code, string state, string? stateCookie)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/auth/github/callback?code={code}&state={state}");
        if (stateCookie is not null)
            request.Headers.Add("Cookie", $"gh_state={stateCookie}; gh_club=");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The state the browser is sent to GitHub with is also the one left in the cookie, which is what
    /// makes the callback check below possible at all.
    /// </summary>
    [Fact]
    public async Task A_start_sends_the_provider_the_same_state_it_left_in_the_browser()
    {
        var start = await Client().GetAsync("/api/auth/github/start", TestContext.Current.CancellationToken);

        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = start.Headers.Location!.ToString();
        location.Should().StartWith("https://github.com/login/oauth/authorize");

        var sentToGitHub = System.Web.HttpUtility.ParseQueryString(
            new Uri(location).Query)["state"];
        sentToGitHub.Should().NotBeNullOrEmpty();
        StateCookieFrom(start).Should().Be(sentToGitHub,
            "the callback compares these two, so a start that did not match them would make the check vacuous");

        System.Web.HttpUtility.ParseQueryString(new Uri(location).Query)["redirect_uri"]
            .Should().Be($"{BaseUrl}/api/auth/github/callback",
                "the redirect_uri comes from the configured base URL, not from the caller's Host header");
    }

    /// <summary>
    /// A callback the browser never started, so there is no cookie to compare against, mints nothing.
    /// </summary>
    [Fact]
    public async Task A_callback_with_no_state_cookie_is_refused()
    {
        var response = await CallbackAsync(
            Client(new GitHubStubHandler("nobody@example.com", verified: true)),
            code: "any-code", state: "any-state", stateCookie: null);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/login?fberror=");
        location.Should().NotContain("#token=", "nothing may be issued for a flow this browser never began");
    }

    /// <summary>
    /// A callback carrying somebody else's state is the CSRF itself, and is refused.
    /// </summary>
    [Fact]
    public async Task A_callback_whose_state_does_not_match_the_cookie_is_refused()
    {
        var response = await CallbackAsync(
            Client(new GitHubStubHandler("nobody@example.com", verified: true)),
            code: "any-code",
            state: Guid.NewGuid().ToString("N"),
            stateCookie: Guid.NewGuid().ToString("N"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().NotContain("#token=");
    }

    /// <summary>
    /// The positive control, and the only reason the three tests above mean anything. A callback that
    /// matches its own state, from a GitHub account with a verified primary address, signs in.
    /// </summary>
    /// <remarks>
    /// Without this, a callback endpoint that redirected to the error page unconditionally would pass
    /// every assertion above while having removed social sign-in from the product.
    /// </remarks>
    [Fact]
    public async Task A_callback_that_matches_its_own_state_signs_a_verified_account_in()
    {
        var email = $"gh-{Guid.NewGuid():n}@example.com";
        var client = Client(new GitHubStubHandler(email, verified: true));

        var start = await client.GetAsync("/api/auth/github/start", TestContext.Current.CancellationToken);
        var state = StateCookieFrom(start);

        var response = await CallbackAsync(client, code: "good-code", state: state, stateCookie: state);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.ToString();
        location.Should().StartWith($"{BaseUrl}/auth/social#token=");
        location.Should().Contain("&refresh=");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<barakoCMS.Models.User>()
                .FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken))
            .Should().NotBeNull("a first sign-in creates the account it signed in");
    }

    /// <summary>
    /// GitHub reports whether it has verified an address, and an unverified one is not a login.
    /// </summary>
    /// <remarks>
    /// The address is the only join key, so accepting an unverified one means anybody who can get
    /// GitHub to accept an address they do not own can sign in as whoever holds it here.
    /// </remarks>
    [Fact]
    public async Task A_callback_for_an_unverified_github_address_is_refused()
    {
        var email = $"gh-unverified-{Guid.NewGuid():n}@example.com";
        var client = Client(new GitHubStubHandler(email, verified: false));

        var start = await client.GetAsync("/api/auth/github/start", TestContext.Current.CancellationToken);
        var state = StateCookieFrom(start);

        var response = await CallbackAsync(client, code: "good-code", state: state, stateCookie: state);

        response.Headers.Location!.ToString().Should().NotContain("#token=");

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        (await session.Query<barakoCMS.Models.User>()
                .FirstOrDefaultAsync(u => u.Email == email, TestContext.Current.CancellationToken))
            .Should().BeNull("a refused sign-in must not leave an account behind for the address to be squatted");
    }

    /// <summary>
    /// A provider that is not configured cannot be started, and is not advertised as if it could be.
    /// </summary>
    [Fact]
    public async Task Only_the_configured_providers_are_advertised_and_startable()
    {
        var client = Client();

        var listed = await client.GetAsync("/api/auth/providers", TestContext.Current.CancellationToken);
        listed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(
            await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("github").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("google").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("facebook").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("linkedin").GetBoolean().Should().BeFalse();

        (await client.GetAsync("/api/auth/google/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a button the client is told not to show must not work if it is pressed anyway");
    }

    /// <summary>
    /// The master switch turns every provider off at once, including the one that is configured.
    /// </summary>
    [Fact]
    public async Task The_master_switch_takes_a_configured_provider_off_too()
    {
        var factory = _fixture.WithSettings(new Dictionary<string, string?>
        {
            { "App:BaseUrl", BaseUrl },
            { "GitHub:ClientId", "test-client-id" },
            { "ExternalAuth:Enabled", "false" },
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ClientIp);

        (await client.GetAsync("/api/auth/github/start", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var document = JsonDocument.Parse(await (await client.GetAsync(
            "/api/auth/providers", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("github").GetBoolean().Should().BeFalse();
    }
}
