using System.Net;
using System.Net.Http.Json;
using Xunit;
using FluentAssertions;
using Marten;
using Marten.Patching;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Integration1")]
public class RefreshLockoutTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    public RefreshLockoutTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(Guid Id, string Refresh)> SignInAsync()
    {
        const string password = "P@ssword123!";
        var id = Guid.NewGuid();
        var username = $"lockrefresh-{id}";
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new barakoCMS.Models.User
            {
                Id = id,
                Username = username,
                Email = $"{username}@test.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password)
            });
            await session.SaveChangesAsync();
        }

        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        resp.EnsureSuccessStatusCode();
        var login = await resp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();
        login!.RefreshToken.Should().NotBeNullOrEmpty();
        return (id, login.RefreshToken);
    }

    private async Task SetLockAsync(Guid id, DateTime? until)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Patch<barakoCMS.Models.User>(id).Set(x => x.LockoutUntil, until);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task A_locked_account_cannot_refresh()
    {
        var (id, refresh) = await SignInAsync();
        await SetLockAsync(id, DateTime.UtcNow.AddMinutes(15));

        var resp = await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = refresh });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_refused_refresh_on_a_locked_account_leaves_the_token_usable_after_the_lock_ends()
    {
        var (id, refresh) = await SignInAsync();
        await SetLockAsync(id, DateTime.UtcNow.AddMinutes(15));
        (await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = refresh }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await SetLockAsync(id, DateTime.UtcNow.AddMinutes(-1));
        var resp = await _client.PostAsJsonAsync("/api/auth/refresh", new { RefreshToken = refresh });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
