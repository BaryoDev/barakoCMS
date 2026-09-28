using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The refresh token reaches a browser in a cookie page script cannot read.
/// </summary>
/// <remarks>
/// The admin stored both tokens in localStorage. The access token is a 15 minute credential and has
/// to be readable, because it is sent as a bearer. The refresh token was the real loss: seven days,
/// renewable, and rotation does not help an attacker who simply keeps refreshing. So one XSS, or one
/// compromised dependency in the admin build, was a week of account takeover.
///
/// The body still carries it, deliberately. A cookie is a browser mechanism and the generated
/// clients, module consumers and anything on a phone read it from the response, so making this a
/// replacement rather than an addition would break every non-browser caller to fix a browser-only
/// problem.
/// </remarks>
[Collection("Sequential")]
public class RefreshCookieTests
{
    private readonly IntegrationTestFixture _factory;

    public RefreshCookieTests(IntegrationTestFixture factory) => _factory = factory;

    private Task<(HttpClient Client, string Username, string Password)> UserAsync(string ip) =>
        UserAsync(ip, handleCookies: true);

    /// <summary>
    /// A user and a client. <paramref name="handleCookies"/> false is the important one.
    /// </summary>
    /// <remarks>
    /// <c>WebApplicationFactoryClientOptions.HandleCookies</c> defaults to true, so a client that
    /// has signed in carries the refresh cookie on every later request whether the test wants it or
    /// not. A test meaning to exercise the body-token path gets the cookie path as well, and passes
    /// through the fallback even if body handling is broken.
    /// </remarks>
    private async Task<(HttpClient Client, string Username, string Password)> UserAsync(
        string ip, bool handleCookies)
    {
        var username = $"ck_{Guid.NewGuid():n}"[..14];
        const string password = "Ck!Passw0rd123";

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            });
            await s.SaveChangesAsync();
        }

        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                HandleCookies = handleCookies,
            });
        // Its own rate-limit bucket, or the suite's other auth traffic refuses this one first.
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return (client, username, password);
    }

    private static string? RefreshCookie(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith("barako_refresh=", StringComparison.Ordinal))
            : null;

    [Fact]
    public async Task Signing_in_sets_an_httponly_refresh_cookie()
    {
        var (client, username, password) = await UserAsync("203.0.113.71");

        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        var cookie = RefreshCookie(res);
        cookie.Should().NotBeNull("the durable credential travels in a cookie now");
        cookie!.ToLowerInvariant().Should().Contain("httponly",
            "page script must not be able to read it, which is the whole mechanism");
        cookie.Should().Contain("path=/api/auth/refresh",
            "scoped to the one route that consumes it, not attached to every API call");
    }

    /// <summary>
    /// A caller with only the cookie can refresh, sending no token in the body.
    /// </summary>
    /// <remarks>
    /// This is what lets the admin hold nothing. The body used to be required by the validator, so
    /// a cookie-only request was refused before the cookie was ever looked at.
    /// </remarks>
    [Fact]
    public async Task A_cookie_alone_is_enough_to_refresh()
    {
        var (client, username, password) = await UserAsync("203.0.113.72");

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.IsSuccessStatusCode.Should().BeTrue();

        var cookie = RefreshCookie(login)!.Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add(TestRemoteIpFilter.Header, "203.0.113.72");

        var refreshed = await client.SendAsync(request);

        refreshed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The body still works, so nothing that is not a browser had to change.
    /// </summary>
    /// <remarks>
    /// The control for the cookie tests, and the reason this is an addition rather than a
    /// replacement. Without it a change that only accepted cookies would pass everything above while
    /// breaking every generated client and anything running on a phone.
    /// </remarks>
    [Fact]
    public async Task The_body_still_carries_the_refresh_token_for_non_browser_callers()
    {
        // Cookies off, or the client attaches the login cookie to the refresh below and the
        // endpoint's cookie fallback answers it. The test would then pass with body-token handling
        // removed entirely, which is the opposite of what it claims to prove.
        var (client, username, password) = await UserAsync("203.0.113.73", handleCookies: false);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var refreshToken = loginDoc.RootElement.GetProperty("refreshToken").GetString();

        refreshToken.Should().NotBeNullOrEmpty("a non-browser caller reads it from the response");

        // No cookie on this request, and none on the client either.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, "203.0.113.73");

        var refreshed = await client.SendAsync(request);

        refreshed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());

        using var refreshedDoc = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        refreshedDoc.RootElement.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty(
            "a caller that sent the token in the body reads the replacement from the body");
    }

    /// <summary>
    /// A caller that refreshed with the cookie gets the replacement in the cookie only.
    /// </summary>
    /// <remarks>
    /// The route is anonymous and the browser attaches the cookie by itself, so any script running
    /// on the console's origin can call it. If the body carried the new token, that script could
    /// read a renewable seven day credential the cookie was meant to keep out of its reach.
    /// </remarks>
    [Fact]
    public async Task A_cookie_refresh_returns_the_new_token_in_the_cookie_and_not_the_body()
    {
        var (client, username, password) = await UserAsync("203.0.113.75", handleCookies: false);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.IsSuccessStatusCode.Should().BeTrue();
        var cookie = RefreshCookie(login)!.Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add("Cookie", cookie);

        var refreshed = await client.SendAsync(request);
        refreshed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());

        using var doc = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("refreshToken").GetString().Should().BeNullOrEmpty(
            "page script can read the body, and the cookie exists so it never holds this value");

        var newCookie = RefreshCookie(refreshed);
        newCookie.Should().NotBeNull("the replacement has to reach the browser somehow");
        newCookie!.Split(';')[0].Should().NotBe(cookie, "rotation issues a new token");
    }

    /// <summary>A browser has to send the cookie to logout as well, or signing out cannot use it.</summary>
    [Fact]
    public async Task Signing_in_scopes_a_refresh_cookie_to_logout_too()
    {
        var (client, username, password) = await UserAsync("203.0.113.76", handleCookies: false);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.IsSuccessStatusCode.Should().BeTrue();

        login.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var cookies = values!.Where(v => v.StartsWith("barako_refresh=", StringComparison.Ordinal)).ToList();
        cookies.Should().NotBeEmpty();
        cookies.Should().Contain(c => c.Contains("path=/api/auth/refresh"));
        cookies.Should().Contain(c => c.Contains("path=/api/auth/logout"));
    }

    /// <summary>A copy of the login token re-signed with an expiry in the past.</summary>
    private static string Expired(string jwt)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var original = handler.ReadJwtToken(jwt);
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Text.Encoding.UTF8.GetBytes(IntegrationTestFixture.JwtKey));
        var expired = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            original.Issuer,
            original.Audiences.First(),
            original.Claims.Where(c => c.Type is not ("exp" or "nbf" or "iat" or "aud" or "iss")),
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(expired);
    }

    /// <summary>
    /// Signing out works with the refresh cookie alone, once the access token has expired.
    /// </summary>
    /// <remarks>
    /// Logout used to require a valid bearer. A console that sat idle past the access token's
    /// fifteen minutes got a 401, revoked nothing, and left the seven day cookie working.
    /// </remarks>
    [Fact]
    public async Task Logout_with_an_expired_bearer_and_the_cookie_revokes_the_refresh_token()
    {
        var (client, username, password) = await UserAsync("203.0.113.77", handleCookies: false);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        login.IsSuccessStatusCode.Should().BeTrue();
        var cookie = RefreshCookie(login)!.Split(';')[0];
        string jwt;
        using (var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            jwt = doc.RootElement.GetProperty("token").GetString()!;

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Authorization = new("Bearer", Expired(jwt));
        logout.Headers.Add("Cookie", cookie);

        var res = await client.SendAsync(logout);
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());
        res.Headers.TryGetValues("Set-Cookie", out var cleared).Should().BeTrue();
        cleared!.Should().Contain(c => c.StartsWith("barako_refresh=;", StringComparison.Ordinal),
            "signing out clears the cookie");

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        refresh.Headers.Add("Cookie", cookie);
        (await client.SendAsync(refresh)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the cookie that signed out must not mint another session");
    }

    /// <summary>
    /// An unknown cookie gets the same answer as a real one, so logout does not test tokens.
    /// </summary>
    [Fact]
    public async Task Logout_with_an_unknown_cookie_answers_like_a_real_one()
    {
        var (client, _, _) = await UserAsync("203.0.113.78", handleCookies: false);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", "barako_refresh=" + Uri.EscapeDataString(Convert.ToBase64String(Guid.NewGuid().ToByteArray())));

        var res = await client.SendAsync(logout);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_with_neither_a_bearer_nor_a_refresh_token_is_still_refused()
    {
        var (client, _, _) = await UserAsync("203.0.113.79", handleCookies: false);

        var res = await client.PostAsync("/api/auth/logout", null);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refreshing_with_neither_a_body_nor_a_cookie_is_refused()
    {
        var (client, _, _) = await UserAsync("203.0.113.74");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, "203.0.113.74");

        var res = await client.SendAsync(request);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the same answer an unknown token gets, because saying which is which tells a prober "
          + "their request was well formed");
    }

    /*
     * Secure is decided by the host environment, not by Request.IsHttps.
     *
     * IsHttps describes the hop that reached the process. Behind a TLS-terminating ingress that is
     * not forwarding headers, a request the user made over https arrives as http, and the cookie
     * would have shipped without Secure on exactly the deployment that needs it most.
     *
     * Tested on the decision rather than end to end. The version of this that stood up a host with
     * UseEnvironment("Production") passed on its own and failed in the full suite, because which
     * environment the derived host resolves to depends on the order tests build their hosts in. A
     * flaky assertion about a security attribute is worse than not having one.
     *
     * Both directions, because always-Secure would pass the production case on its own and break
     * every local http stack.
     */

    private static HttpContext ContextOn(string environment)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new StubEnvironment { EnvironmentName = environment });
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "BarakoCMS.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public void The_refresh_cookie_is_Secure_on_a_production_host()
    {
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.IsSecure(ContextOn(Environments.Production))
            .Should().BeTrue(
                "the request can reach a production host over plain http through a proxy that "
                + "terminated TLS, so following Request.IsHttps would drop Secure exactly there");
    }

    [Fact]
    public void The_refresh_cookie_is_not_Secure_on_a_development_host()
    {
        barakoCMS.Infrastructure.Auth.RefreshTokenCookie.IsSecure(ContextOn(Environments.Development))
            .Should().BeFalse(
                "a Secure cookie is not sent over http, so marking it here breaks every local stack");
    }
}
