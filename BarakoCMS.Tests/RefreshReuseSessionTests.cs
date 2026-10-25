using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Presenting a refresh token that was already used ends the whole session: the user's other
/// refresh tokens and the access tokens issued before it.
/// </summary>
[Collection("Integration1")]
public class RefreshReuseSessionTests(IntegrationTestFixture factory)
{
    private static int _ipCounter;

    private async Task<(Guid Id, string Access)> SeedUserWithAccessTokenAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == "SuperAdmin");
        if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = "SuperAdmin" }; session.Store(role); }

        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"reuse_{id:n}",
            Email = $"reuse_{id:n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var issuer = scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Auth.ITokenIssuer>();
        var result = await issuer.IssueAccessTokenAsync(user, "default");
        result.Allowed.Should().BeTrue();
        return (id, result.Token);
    }

    private async Task<string> StoreRefreshTokenAsync(Guid userId)
    {
        var plain = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = RefreshToken.HashOf(plain),
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await session.SaveChangesAsync();
        return plain;
    }

    private HttpClient Client(string? access = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"2001:db8:5400:{Interlocked.Increment(ref _ipCounter):x}::1");
        if (access is not null)
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        return client;
    }

    private async Task<int> LiveRefreshTokensAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<RefreshToken>().CountAsync(t => t.UserId == userId && !t.IsRevoked);
    }

    [Fact]
    public async Task An_access_token_issued_before_a_refresh_token_was_reused_is_refused()
    {
        var (id, access) = await SeedUserWithAccessTokenAsync();
        var first = await StoreRefreshTokenAsync(id);

        (await Client(access).GetAsync("/api/contents", TestContext.Current.CancellationToken))
            .StatusCode.Should().NotBe(HttpStatusCode.Unauthorized, "the control: the token works to start with");

        // iat is whole seconds and a same-second token is served by design, so the reuse has to land
        // in a later second than the token's.
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);

        var rotate = await Client().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = first }, TestContext.Current.CancellationToken);
        rotate.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await Client().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = first }, TestContext.Current.CancellationToken);
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await Client(access).GetAsync("/api/contents", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "reuse ends the session, so a token issued before it stops working");
    }

    [Fact]
    public async Task Two_refreshes_of_one_token_at_once_leave_no_live_refresh_token()
    {
        for (var round = 0; round < 3; round++)
        {
            var (id, _) = await SeedUserWithAccessTokenAsync();
            var token = await StoreRefreshTokenAsync(id);

            var responses = await Task.WhenAll(
                Client().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = token }, TestContext.Current.CancellationToken),
                Client().PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = token }, TestContext.Current.CancellationToken));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(1,
                "one token rotates once");
            responses.Should().HaveCount(2);
            (await LiveRefreshTokensAsync(id)).Should().Be(0,
                "whichever request lost, the user is left with no live refresh token");
        }
    }
}
