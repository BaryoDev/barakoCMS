using Xunit;
using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Self-service password change (POST /api/me/password) and admin reset (POST /api/users/{id}/password)
/// against the real API over real Postgres. Adversarial: a wrong current password, a weak new password,
/// and a no-op change are all rejected; the admin reset needs SuperAdmin; and after a change the old
/// password stops working while the new one logs in.
/// </summary>
[Collection("Sequential")]
public class ChangePasswordTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ChangePasswordTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<(User user, string password)> SeedUserAsync(string password = "OldPassw0rd!x")
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var user = new User
        {
            Id = id,
            Username = $"user-{id}",
            Email = $"user-{id}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        };
        s.Store(user);
        await s.SaveChangesAsync();
        return (user, password);
    }

    private void As(string token) =>
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task ChangePassword_Succeeds_AndOldPasswordStopsWorking()
    {
        var (user, oldPw) = await SeedUserAsync();
        As(_factory.CreateToken(new[] { "User" }, user.Id.ToString()));

        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = oldPw, NewPassword = "BrandNewP@ss99" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());

        // The stored hash now verifies the new password, not the old one.
        using var scope = _factory.Services.CreateScope();
        var q = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var updated = await q.LoadAsync<User>(user.Id);
        BCrypt.Net.BCrypt.Verify("BrandNewP@ss99", updated!.PasswordHash).Should().BeTrue();
        BCrypt.Net.BCrypt.Verify(oldPw, updated.PasswordHash).Should().BeFalse();
    }

    [Fact]
    public async Task ChangePassword_RevokesExistingRefreshTokens()
    {
        var (user, oldPw) = await SeedUserAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            tokenId = Guid.NewGuid();
            s.Store(new RefreshToken { Id = tokenId, Token = $"tok-{tokenId}", UserId = user.Id, ExpiresAt = DateTime.UtcNow.AddDays(7), IsRevoked = false });
            await s.SaveChangesAsync();
        }

        As(_factory.CreateToken(new[] { "User" }, user.Id.ToString()));
        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = oldPw, NewPassword = "BrandNewP@ss99" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verify = _factory.Services.CreateScope();
        var q = verify.ServiceProvider.GetRequiredService<IQuerySession>();
        var tok = await q.LoadAsync<RefreshToken>(tokenId);
        tok!.IsRevoked.Should().BeTrue("a password change must invalidate existing sessions");
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_IsRejected()
    {
        var (user, _) = await SeedUserAsync();
        As(_factory.CreateToken(new[] { "User" }, user.Id.ToString()));

        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = "not-it", NewPassword = "BrandNewP@ss99" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_WeakNew_IsRejected()
    {
        var (user, oldPw) = await SeedUserAsync();
        As(_factory.CreateToken(new[] { "User" }, user.Id.ToString()));

        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = oldPw, NewPassword = "short" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_SameAsCurrent_IsRejected()
    {
        var (user, oldPw) = await SeedUserAsync();
        As(_factory.CreateToken(new[] { "User" }, user.Id.ToString()));

        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = oldPw, NewPassword = oldPw });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_Anonymous_IsUnauthorized()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var res = await _client.PostAsJsonAsync("/api/me/password", new { CurrentPassword = "x", NewPassword = "BrandNewP@ss99" });
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminReset_AsSuperAdmin_SetsNewPassword()
    {
        var (user, oldPw) = await SeedUserAsync();
        // A user that exists, holding the seeded SuperAdmin role: resetting somebody else's password
        // is manage_users, which is answered from the stored user rather than from the claim.
        As(await _factory.StoredUserTokenAsync("SuperAdmin"));

        var res = await _client.PostAsJsonAsync($"/api/users/{user.Id}/password", new { NewPassword = "ResetByAdm1n!" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, because: await res.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var q = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var updated = await q.LoadAsync<User>(user.Id);
        BCrypt.Net.BCrypt.Verify("ResetByAdm1n!", updated!.PasswordHash).Should().BeTrue();
        BCrypt.Net.BCrypt.Verify(oldPw, updated.PasswordHash).Should().BeFalse();
    }

    [Fact]
    public async Task AdminReset_WithoutSuperAdmin_IsForbidden()
    {
        var (user, _) = await SeedUserAsync();
        As(_factory.CreateToken(new[] { "User" }, Guid.NewGuid().ToString()));

        var res = await _client.PostAsJsonAsync($"/api/users/{user.Id}/password", new { NewPassword = "ResetByAdm1n!" });
        res.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
