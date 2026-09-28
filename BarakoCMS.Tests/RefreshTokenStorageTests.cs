using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Refresh tokens are stored as a hash, so reading the table does not hand out working sessions.
/// </summary>
/// <remarks>
/// Rows written before hashing hold the token in plain text. They keep working until their next
/// refresh, which replaces them with a hashed row, so a deploy signs nobody out and the last plain
/// row is gone within the seven day lifetime.
/// </remarks>
[Collection("Sequential")]
public class RefreshTokenStorageTests
{
    private const string Password = "Rt!Passw0rd123";
    private readonly IntegrationTestFixture _factory;

    public RefreshTokenStorageTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid UserId, string Username)> UserAsync(string ip)
    {
        var userId = Guid.NewGuid();
        var username = $"rt_{Guid.NewGuid():n}"[..14];
        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            s.Store(new User
            {
                Id = userId,
                Username = username,
                Email = $"{username}@example.com",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            });
            await s.SaveChangesAsync();
        }

        // No cookie jar, so every refresh below is decided by the body token alone.
        var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return (client, userId, username);
    }

    private async Task<Guid> StoreLegacyRowAsync(Guid userId, string plaintext)
    {
        var id = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new RefreshToken
        {
            Id = id,
            Token = plaintext,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
        });
        await s.SaveChangesAsync();
        return id;
    }

    /// <summary>Every string value in the user's stored refresh token documents, raw JSON.</summary>
    private async Task<List<string>> StoredStringsAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var ids = await s.Query<RefreshToken>().Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync();
        ids.Should().NotBeEmpty("the check below has to run on a stored row");

        var values = new List<string>();
        foreach (var id in ids)
        {
            var json = await s.Json.FindByIdAsync<RefreshToken>(id);
            json.Should().NotBeNull();
            using var doc = JsonDocument.Parse(json!);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    values.Add(p.Value.GetString()!);
        }
        return values;
    }

    private static async Task<string> RefreshTokenFrom(HttpResponseMessage res)
    {
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("refreshToken").GetString();
        token.Should().NotBeNullOrEmpty();
        return token!;
    }

    [Fact]
    public async Task A_stored_refresh_token_does_not_contain_the_token_itself()
    {
        var (client, userId, username) = await UserAsync("203.0.113.81");

        var issued = await RefreshTokenFrom(
            await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password }));

        var stored = await StoredStringsAsync(userId);
        stored.Should().NotContain(issued, "a copy of the database must not be a set of working sessions");

        // And the hashed row is the one that answers: the token still refreshes.
        var rotated = await RefreshTokenFrom(
            await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = issued }));
        (await StoredStringsAsync(userId)).Should().NotContain(rotated);
    }

    [Fact]
    public async Task A_plaintext_row_from_before_hashing_refreshes_once_and_its_replacement_is_hashed()
    {
        var (client, userId, _) = await UserAsync("203.0.113.82");
        var legacy = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "+/legacy";
        await StoreLegacyRowAsync(userId, legacy);

        var replacement = await RefreshTokenFrom(
            await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = legacy }));

        (await StoredStringsAsync(userId)).Should().NotContain(replacement,
            "rotation is how plaintext rows age out, so the row replacing one must be hashed");

        var again = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = legacy });
        again.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "a rotated token is spent");

        // Replaying the spent legacy token revokes the family, so the hashed replacement dies too.
        var afterReuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = replacement });
        afterReuse.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_users_tokens_covers_hashed_and_plaintext_rows()
    {
        var (client, userId, username) = await UserAsync("203.0.113.83");
        var legacy = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "legacy";
        await StoreLegacyRowAsync(userId, legacy);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = Password });
        var hashed = await RefreshTokenFrom(login);
        using (var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
        {
            using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            logout.Headers.Authorization = new("Bearer", doc.RootElement.GetProperty("token").GetString());
            (await client.SendAsync(logout)).IsSuccessStatusCode.Should().BeTrue();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var rows = await s.Query<RefreshToken>().Where(t => t.UserId == userId).ToListAsync();
            rows.Should().HaveCount(2, "one legacy row and one issued at login");
            rows.Should().OnlyContain(t => t.IsRevoked);
        }

        (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = legacy }))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = hashed }))
            .StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }
}
