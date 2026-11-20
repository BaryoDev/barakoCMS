using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A client of a fixture host gets its own global limiter bucket (#1392). Under the test server a
/// request has no remote address, so every client that sent none shared the "unknown" bucket, and a
/// class waited on, or was refused for, the traffic of the classes before it.
/// </summary>
[Collection("Integration1")]
public class FixtureClientLimiterBucketTests
{
    private const string Password = "P@ssword123!Ab";
    private const string QueueLimitKey = RateLimitSetup.Section + ":Global:QueueLimit";

    private readonly IntegrationTestFixture _fixture;

    public FixtureClientLimiterBucketTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The queue is off so a request over the window is refused at once. With one, it waits for the
    /// next window, and a client that shared a spent bucket would hang for a minute instead of failing.
    /// </summary>
    [Fact]
    public async Task A_second_client_is_answered_at_once_after_the_first_used_up_the_window()
    {
        var host = _fixture.WithSettings(new Dictionary<string, string?> { [QueueLimitKey] = "0" });
        var window = RateLimitSetup.Read(host.Services.GetRequiredService<IConfiguration>()).Global;
        window.QueueLimit.Should().Be(0);

        var first = host.CreateClient();
        var second = host.CreateClient();

        for (var i = 0; i < window.PermitLimit; i++)
        {
            (await first.GetAsync("/api/meta", Ct)).StatusCode
                .Should().NotBe(HttpStatusCode.TooManyRequests, "request {0} is inside the window of {1}", i + 1, window.PermitLimit);
        }

        (await first.GetAsync("/api/meta", Ct)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests, "the first client has used the whole window");

        (await second.GetAsync("/api/meta", Ct)).StatusCode
            .Should().NotBe(HttpStatusCode.TooManyRequests, "the second client has a bucket of its own");
    }

    [Fact]
    public async Task Clients_of_the_fixture_sign_in_from_different_addresses_in_the_reserved_range()
    {
        var first = await SignInAddressAsync(_fixture.CreateClient());
        var second = await SignInAddressAsync(_fixture.CreateClient());

        first.Should().NotBe(second);
        foreach (var address in new[] { first, second })
        {
            var bytes = IPAddress.Parse(address).GetAddressBytes();
            (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)).Should().BeTrue("{0} is in 198.18.0.0/15", address);
        }
    }

    [Fact]
    public async Task A_client_that_sends_its_own_address_keeps_it()
    {
        const string own = "192.0.2.77";
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, own);

        (await SignInAddressAsync(client)).Should().Be(own);
    }

    private async Task<string> SignInAddressAsync(HttpClient client)
    {
        var id = Guid.NewGuid();
        var username = $"bucket-{id:N}";
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = id,
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
                RoleIds = [],
            });
            await session.SaveChangesAsync(Ct);
        }

        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password }, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!.Token!;

        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/me/sessions");
        list.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(list, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return page.GetProperty("items").EnumerateArray().Single().GetProperty("signInIp").GetString()!;
    }
}
