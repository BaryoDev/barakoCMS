using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
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

    private async Task<(HttpClient Client, string Username, string Password)> UserAsync(string ip)
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

        var client = _factory.CreateClient();
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
        var (client, username, password) = await UserAsync("203.0.113.73");

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        using var loginDoc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var refreshToken = loginDoc.RootElement.GetProperty("refreshToken").GetString();

        refreshToken.Should().NotBeNullOrEmpty("a non-browser caller reads it from the response");

        // No cookie on this request at all.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken }),
        };
        request.Headers.Add(TestRemoteIpFilter.Header, "203.0.113.73");

        var refreshed = await client.SendAsync(request);

        refreshed.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync());
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
}
