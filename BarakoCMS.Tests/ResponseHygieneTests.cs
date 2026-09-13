using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Responses that carry a token, a key or the caller's own details must not be kept by a browser or
/// a proxy cache (#654, OWASP ASVS 8.2.1). Every other response keeps whatever caching it had.
/// </summary>
[Collection("Sequential")]
public class ResponseHygieneTests
{
    private const string Password = "P@ssword123!";

    private readonly IntegrationTestFixture _factory;

    public ResponseHygieneTests(IntegrationTestFixture factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        // The login route is rate limited per client address, so each client gets its own.
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.100.{Random.Shared.Next(1, 250)}");
        return client;
    }

    private async Task<string> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var username = $"hygiene-{Guid.NewGuid():n}";
        session.Store(new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return username;
    }

    private static void ShouldNotBeStored(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull("a response carrying a secret must say how it may be cached");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Pragma.Select(p => p.Name).Should().Contain("no-cache",
            "HTTP/1.0 caches read Pragma, not Cache-Control");
    }

    [Fact]
    public async Task A_login_response_carrying_tokens_is_not_stored()
    {
        var client = Client();
        var username = await CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = username, Password }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var login = await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(
            TestContext.Current.CancellationToken);
        login!.Token.Should().NotBeNullOrEmpty("the body this header protects has to actually hold a token");
        ShouldNotBeStored(response);
    }

    [Fact]
    public async Task A_refresh_response_carrying_tokens_is_not_stored()
    {
        var client = Client();
        var username = await CreateUserAsync();
        var login = await (await client.PostAsJsonAsync("/api/auth/login",
                new { Username = username, Password }, TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(TestContext.Current.CancellationToken);

        var response = await client.PostAsJsonAsync("/api/auth/refresh",
            new { RefreshToken = login!.RefreshToken }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldNotBeStored(response);
    }

    [Fact]
    public async Task A_failed_login_is_not_stored_either()
    {
        var client = Client();
        var username = await CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { Username = username, Password = "not-the-password" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ShouldNotBeStored(response);
    }

    [Fact]
    public async Task The_callers_own_profile_is_not_stored()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("User"));

        var response = await client.GetAsync("/api/me/profile", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldNotBeStored(response);
    }

    [Fact]
    public async Task A_created_api_key_is_not_stored()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        var response = await client.PostAsJsonAsync("/api/api-keys",
            new { name = "hygiene key", scopes = new[] { "content:read" } }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        ShouldNotBeStored(response);
    }

    /// <summary>
    /// The control. Marking every response no-store would pass every test above and throw away the
    /// short public cache the delivery API depends on.
    /// </summary>
    [Fact]
    public async Task A_public_response_keeps_its_own_caching()
    {
        var client = Client();

        var response = await client.GetAsync("/api/public/sitemap.xml", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (response.Headers.CacheControl?.NoStore ?? false).Should().BeFalse();
        response.Headers.Pragma.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/auth/login", true)]
    [InlineData("/api/auth/refresh", true)]
    [InlineData("/api/auth/mfa/setup", true)]
    [InlineData("/api/auth/github/callback", true)]
    [InlineData("/api/me/profile", true)]
    [InlineData("/api/me/switch", true)]
    [InlineData("/api/api-keys", true)]
    [InlineData("/api/preview", true)]
    [InlineData("/API/AUTH/LOGIN", true)]
    [InlineData("/api/authors", false)]
    [InlineData("/api/media", false)]
    [InlineData("/api/public/sitemap.xml", false)]
    [InlineData("/api/contents", false)]
    public void Only_token_key_and_self_routes_are_marked_no_store(string path, bool expected)
    {
        SecurityHeaders.IsNoStorePath(path).Should().Be(expected);
    }

    /// <summary>
    /// The test server does not write a Server header at all, so asserting its absence on a response
    /// here would pass with or without the setting. The setting is what reaches Kestrel.
    /// </summary>
    [Fact]
    public void Kestrel_does_not_announce_itself()
    {
        _factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value
            .AddServerHeader.Should().BeFalse();
    }
}
