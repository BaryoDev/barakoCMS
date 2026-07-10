using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class AuthTokenReuseTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public AuthTokenReuseTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> CreateUserWithPasswordAsync(string password)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var username = $"tokentest-{Guid.NewGuid()}";
        session.Store(new barakoCMS.Models.User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
        });
        await session.SaveChangesAsync();
        return username;
    }

    [Fact]
    public async Task RefreshToken_Reuse_IsRejected_AndRevokesTokenFamily()
    {
        // Arrange: a user we can log in as.
        const string password = "P@ssword123!";
        var username = await CreateUserWithPasswordAsync(password);

        // 1. Login to obtain the first refresh token (R1).
        var loginResp = await _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        loginResp.EnsureSuccessStatusCode();
        var login = await loginResp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();
        var r1 = login!.RefreshToken;
        r1.Should().NotBeNullOrEmpty();

        // 2. Rotate R1 -> R2. This succeeds and marks R1 as "used".
        var refresh1 = await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = r1 });
        refresh1.EnsureSuccessStatusCode();
        var rotated = await refresh1.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Refresh.Response>();
        var r2 = rotated!.RefreshToken;
        r2.Should().NotBeNullOrEmpty();
        r2.Should().NotBe(r1);

        // 3. Replaying the already-used R1 must be rejected (reuse detection).
        var reuse = await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = r1 });
        reuse.IsSuccessStatusCode.Should().BeFalse("a rotated refresh token cannot be used again");

        // 4. Reuse detection must revoke the whole family, so the freshly rotated R2 is now dead too.
        var afterReuse = await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = r2 });
        afterReuse.IsSuccessStatusCode.Should().BeFalse("detecting reuse must revoke every active token for the user");
    }

    [Fact]
    public async Task FailedLoginAttempts_AtomicIncrement_IsNotLostAcrossWrites()
    {
        // The login lockout counter relies on an atomic SQL-level increment so concurrent failed
        // attempts can't be lost (which would bypass lockout). Verify the primitive accumulates.
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new barakoCMS.Models.User
            {
                Id = userId,
                Username = $"pw-{userId}",
                Email = $"{userId}@test.com"
            });
            await session.SaveChangesAsync();

            for (var i = 0; i < 3; i++)
            {
                session.Patch<barakoCMS.Models.User>(userId).Increment(x => x.FailedLoginAttempts);
                await session.SaveChangesAsync();
            }
        }

        using var readScope = _factory.Services.CreateScope();
        var query = readScope.ServiceProvider.GetRequiredService<IQuerySession>();
        var reloaded = await query.LoadAsync<barakoCMS.Models.User>(userId);
        reloaded!.FailedLoginAttempts.Should().Be(3);
    }
}
