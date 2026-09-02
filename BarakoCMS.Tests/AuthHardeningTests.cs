using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Two auth defects that degraded quietly rather than failing.
/// </summary>
[Collection("Sequential")]
public class AuthHardeningTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public AuthHardeningTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// Refresh-token rotation carries the device binding to the replacement token.
    /// </summary>
    /// <remarks>
    /// It did not, so the binding survived exactly one refresh: the token being exchanged still had
    /// a DeviceId and the one replacing it did not. From the second refresh onward device trust had
    /// nothing to enforce against, and nothing anywhere said so.
    ///
    /// The assertion is on the stored replacement rather than on the issued JWT, because the JWT of
    /// the first refresh reads the binding off the OLD token and looks correct either way. That is
    /// what made this survive: the symptom is one rotation later than the cause.
    /// </remarks>
    [Fact]
    public async Task Refresh_rotation_carries_the_device_binding_to_the_new_token()
    {
        var userId = Guid.NewGuid();
        var tokenValue = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        const string deviceId = "device-under-test";

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User
            {
                Id = userId,
                Username = $"rot_{Guid.NewGuid():n}",
                Email = $"rot_{Guid.NewGuid():n}@example.com",
            });
            s.Store(new RefreshToken
            {
                Id = Guid.NewGuid(),
                Token = tokenValue,
                UserId = userId,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow,
                IsRevoked = false,
                DeviceId = deviceId,
            });
            await s.SaveChangesAsync();
        }

        // Its own client IP, so the auth rate limiter partitions this test into its own bucket.
        // Without it the suite packs enough auth calls into one 15 minute window that this gets a
        // 429 and fails for a reason that has nothing to do with device bindings. See
        // TestRemoteIpFilter, which exists for exactly this.
        _client.DefaultRequestHeaders.Remove(TestRemoteIpFilter.Header);
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "203.0.113.41");

        var res = await _client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokenValue });
        res.StatusCode.Should().NotBe(System.Net.HttpStatusCode.TooManyRequests,
            "this must fail on the binding, not on a shared rate limit bucket");
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var replacement = await s.Query<RefreshToken>()
                .Where(t => t.UserId == userId && !t.IsRevoked)
                .FirstOrDefaultAsync();

            replacement.Should().NotBeNull("rotation issues a replacement");
            replacement!.DeviceId.Should().Be(deviceId,
                "a replacement with no device binding is a token device trust can no longer check, "
              + "and the next refresh would produce another one just like it");
        }
    }

    /// <summary>
    /// Requesting a code for an address that does not exist answers exactly like one that does.
    /// </summary>
    /// <remarks>
    /// Pinned because the neighbouring fix pulled the other way. The device approval path now
    /// reports an email send failure, which is right there because the password was already proved.
    /// Doing the same here would tell an unauthenticated caller which addresses are real, so this
    /// route stays neutral and this test is what stops somebody making the two consistent.
    /// </remarks>
    [Fact]
    public async Task Requesting_a_code_does_not_reveal_whether_the_address_exists()
    {
        var real = $"real_{Guid.NewGuid():n}@example.com";
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User { Id = Guid.NewGuid(), Username = $"u_{Guid.NewGuid():n}", Email = real });
            await s.SaveChangesAsync();
        }

        _client.DefaultRequestHeaders.Remove(TestRemoteIpFilter.Header);
        _client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "203.0.113.42");

        var known = await _client.PostAsJsonAsync("/api/auth/otp/request", new { email = real });
        var unknown = await _client.PostAsJsonAsync("/api/auth/otp/request",
            new { email = $"nobody_{Guid.NewGuid():n}@example.com" });

        known.StatusCode.Should().Be(unknown.StatusCode, "the status must not distinguish them");
        (await known.Content.ReadAsStringAsync())
            .Should().Be(await unknown.Content.ReadAsStringAsync(), "nor the body");
    }

    /// <summary>
    /// A failed sign-in tells the caller nothing about the account, including its address.
    /// </summary>
    /// <remarks>
    /// This is the boundary that decided #271's "device-approval response leaks the account email".
    /// The field stays. It is written on one response only, the one reached after the password has
    /// already been verified, so it hands a caller nothing they had not already proved; and
    /// /api/auth/otp/verify is keyed on the address, while the sign-in form collects a username, so
    /// the client genuinely cannot supply it from what it typed.
    ///
    /// What actually matters is that the address never appears one step earlier, and that is what
    /// this pins. If somebody later moves the email onto the failure path, the reasoning above stops
    /// holding and this test is what says so.
    /// </remarks>
    [Fact]
    public async Task A_wrong_password_does_not_return_the_account_address()
    {
        var email = $"addr_{Guid.NewGuid():n}@example.com";
        var username = $"addr_{Guid.NewGuid():n}";

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("TheRealPassword123!"),
                RoleIds = [],
            });
            await s.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(TestRemoteIpFilter.Header);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, "203.0.113.43");

        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Username = username, Password = "NotThePassword123!" });

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(email,
            "the address may only be returned on the device-approval response, which is reached "
          + "after the password has been verified");
    }
}
