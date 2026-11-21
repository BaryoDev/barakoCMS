using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A refresh answer lost on the network can be retried once with the same token inside a short
/// window, without revoking every session (#1391). Anything else is reuse, as before.
/// </summary>
[Collection("Integration1")]
public class RefreshRetryTests(IntegrationTestFixture factory)
{
    private const string Password = "P@ssword123!Ab";
    private static int _ip;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Seeded(Guid Id, string Username);

    private async Task<Seeded> SeedUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var username = $"retry-{id:N}";
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = [Guid.Parse("00000000-0000-0000-0000-000000000001")],
        });
        await session.SaveChangesAsync(Ct);
        return new Seeded(id, username);
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string? device = null, string? cookie = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1391:{Interlocked.Increment(ref _ip):x}::1");
        if (device is not null)
            client.DefaultRequestHeaders.Add("X-Device-Id", device);
        if (cookie is not null)
            client.DefaultRequestHeaders.Add("Cookie", $"{RefreshTokenCookieName}={cookie}");
        return client;
    }

    private const string RefreshTokenCookieName = "barako_refresh";

    private async Task<string> LoginAsync(Seeded user, string? device = null, WebApplicationFactory<Program>? host = null)
    {
        var response = await Client(host ?? factory, device).PostAsJsonAsync(
            "/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = user.Username, Password = Password }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!;
        return body.RefreshToken!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refresh, string? device = null, WebApplicationFactory<Program>? host = null) =>
        Client(host ?? factory, device).PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }, Ct);

    private async Task<string> RotateAsync(string refresh, string? device = null, WebApplicationFactory<Program>? host = null)
    {
        var response = await RefreshAsync(refresh, device, host);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("refreshToken").GetString()!;
    }

    private async Task<RefreshToken> RowAsync(string plain)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var hash = RefreshToken.HashOf(plain);
        return await session.Query<RefreshToken>().FirstAsync(t => t.TokenHash == hash, Ct);
    }

    private async Task EditRowAsync(string plain, Action<RefreshToken> edit)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var hash = RefreshToken.HashOf(plain);
        var row = await session.Query<RefreshToken>().FirstAsync(t => t.TokenHash == hash, Ct);
        edit(row);
        session.Store(row);
        await session.SaveChangesAsync(Ct);
    }

    private Task AgeRotationAsync(string plain, TimeSpan by) =>
        EditRowAsync(plain, row => row.RevokedAt = row.RevokedAt!.Value - by);

    private async Task<int> LiveAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<RefreshToken>().CountAsync(t => t.UserId == userId && !t.IsRevoked, Ct);
    }

    private async Task AssertEverySessionRevokedAsync(Guid userId, params string[] tokens)
    {
        (await LiveAsync(userId)).Should().Be(0, "reuse revokes every session");
        foreach (var token in tokens)
            (await RefreshAsync(token)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_replay_of_the_token_just_rotated_succeeds_and_other_sessions_stay_alive()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);

        var retry = await RefreshAsync(a1);

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var a3 = (await retry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refreshToken").GetString()!;
        a3.Should().NotBe(a2).And.NotBe(a1);
        (await RefreshAsync(b1)).StatusCode.Should().Be(HttpStatusCode.OK, "the other session is untouched");
    }

    [Fact]
    public async Task After_a_retry_the_first_successor_answers_401_and_nothing_else_is_revoked()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);
        var a3 = await RotateAsync(a1);

        (await RefreshAsync(a2)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RowAsync(a2)).RevokedReason.Should().Be("superseded");

        (await LiveAsync(user.Id)).Should().Be(2, "one live refresh token for each of the two sessions");
        (await RefreshAsync(b1)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(a3)).StatusCode.Should().Be(HttpStatusCode.OK, "the new successor is live");
    }

    [Fact]
    public async Task The_retry_keeps_the_session_id_and_is_audited_as_its_own_event()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var sid = (await RowAsync(a1)).SessionId;
        var a2 = await RotateAsync(a1);
        var a3 = await RotateAsync(a1);

        (await RowAsync(a2)).SessionId.Should().Be(sid);
        (await RowAsync(a3)).SessionId.Should().Be(sid);

        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var actions = await session.Query<AuditEvent>().Where(e => e.ActorUserId == user.Id).Select(e => e.Action).ToListAsync(Ct);
        actions.Should().Contain("auth.token.refresh_retried");
        actions.Should().NotContain("auth.token.reuse_detected");
    }

    [Fact]
    public async Task A_replay_after_the_window_revokes_every_session()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);
        await AgeRotationAsync(a1, TimeSpan.FromSeconds(RefreshRetryWindow.DefaultSeconds + 5));

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, a2, b1);
    }

    [Fact]
    public async Task A_replay_after_the_successor_was_used_revokes_every_session()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);
        var a3 = await RotateAsync(a2);

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, a3, b1);
    }

    [Fact]
    public async Task A_second_replay_of_the_same_token_inside_the_window_revokes_every_session()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        await RotateAsync(a1);
        var a3 = await RotateAsync(a1);

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, a3, b1);
    }

    [Fact]
    public async Task A_replay_from_another_device_on_a_bound_row_revokes_every_session()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user, device: "phone-1");
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1, device: "phone-1");

        (await RefreshAsync(a1, device: "phone-2")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, a2, b1);
    }

    [Fact]
    public async Task A_replay_from_the_same_bound_device_is_a_retry()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user, device: "phone-1");
        await RotateAsync(a1, device: "phone-1");

        (await RefreshAsync(a1, device: "phone-1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_row_with_no_session_id_never_gets_the_retry()
    {
        var user = await SeedUserAsync();
        var b1 = await LoginAsync(user);
        var oldPlain = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var nextPlain = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var next = new RefreshToken
        {
            Id = Guid.NewGuid(), TokenHash = RefreshToken.HashOf(nextPlain), UserId = user.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            SessionId = Guid.Empty,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(next);
            session.Store(new RefreshToken
            {
                Id = Guid.NewGuid(), TokenHash = RefreshToken.HashOf(oldPlain), UserId = user.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(1), IsRevoked = true, RevokedReason = "used",
                RevokedAt = DateTime.UtcNow, SuccessorId = next.Id,
            });
            await session.SaveChangesAsync(Ct);
        }

        (await RefreshAsync(oldPlain)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, nextPlain, b1);
    }

    [Fact]
    public async Task A_used_row_whose_successor_is_on_another_session_gets_no_retry()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        await RotateAsync(a1);
        var other = await RowAsync(b1);
        await EditRowAsync(a1, row => row.SuccessorId = other.Id);

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, b1);
    }

    [Fact]
    public async Task A_used_row_whose_successor_belongs_to_another_user_gets_no_retry()
    {
        var user = await SeedUserAsync();
        var stranger = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var sid = (await RowAsync(a1)).SessionId;
        await RotateAsync(a1);
        var foreignPlain = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64));
        var foreign = new RefreshToken
        {
            Id = Guid.NewGuid(), TokenHash = RefreshToken.HashOf(foreignPlain), UserId = stranger.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(1), SessionId = sid,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(foreign);
            await session.SaveChangesAsync(Ct);
        }
        await EditRowAsync(a1, row => row.SuccessorId = foreign.Id);

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await LiveAsync(user.Id)).Should().Be(0);
        (await LiveAsync(stranger.Id)).Should().Be(1, "the stranger's token was not touched");
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("admin_revoke")]
    public async Task A_successor_revoked_for_another_reason_gets_no_retry(string reason)
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);
        await EditRowAsync(a2, row => { row.IsRevoked = true; row.RevokedReason = reason; row.RevokedAt = DateTime.UtcNow; });

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, b1);
    }

    [Fact]
    public async Task A_successor_ended_with_its_session_gets_no_retry()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var sid = (await RowAsync(a1)).SessionId!.Value;
        await RotateAsync(a1);
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            (await SessionRevocation.EndAsync(session, null, user.Id, sid, Ct)).Should().BeTrue();
        }

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_used_row_with_a_session_id_and_no_successor_id_gets_no_retry()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        var b1 = await LoginAsync(user);
        var a2 = await RotateAsync(a1);
        await EditRowAsync(a1, row => row.SuccessorId = null);
        (await RowAsync(a1)).SessionId.Should().NotBeNull();

        (await RefreshAsync(a1)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await AssertEverySessionRevokedAsync(user.Id, a2, b1);
    }

    [Fact]
    public async Task A_retry_racing_a_refresh_of_the_successor_never_leaves_two_live_rows_on_the_session()
    {
        // Which request wins is timing, so this holds the invariant and not an outcome.
        for (var round = 0; round < 3; round++)
        {
            var user = await SeedUserAsync();
            var a1 = await LoginAsync(user);
            var a2 = await RotateAsync(a1);

            var responses = await Task.WhenAll(RefreshAsync(a1), RefreshAsync(a2));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(1);
            (await LiveAsync(user.Id)).Should().BeLessThanOrEqualTo(1);
        }
    }

    [Fact]
    public async Task A_cookie_only_retry_gets_no_refresh_token_in_the_body()
    {
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user);
        await RotateAsync(a1);

        var retry = await Client(factory, cookie: a1).PostAsJsonAsync("/api/auth/refresh", new { }, Ct);

        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("refreshToken").GetString().Should().BeNullOrEmpty();
        retry.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith(RefreshTokenCookieName + "="));
    }

    [Fact]
    public async Task Window_zero_turns_the_retry_off()
    {
        var host = factory.WithSetting(RefreshRetryWindow.Key, "0");
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user, host: host);
        var b1 = await LoginAsync(user, host: host);
        var a2 = await RotateAsync(a1, host: host);

        (await RefreshAsync(a1, host: host)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await LiveAsync(user.Id)).Should().Be(0);
        (await RefreshAsync(a2, host: host)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(b1, host: host)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Two_refreshes_of_one_token_at_once_still_revoke_the_user()
    {
        // Window off, so a request that loads after the other committed is reuse and not a retry.
        var host = factory.WithSetting(RefreshRetryWindow.Key, "0");
        var user = await SeedUserAsync();
        var a1 = await LoginAsync(user, host: host);

        var responses = await Task.WhenAll(RefreshAsync(a1, host: host), RefreshAsync(a1, host: host));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(1);
        (await LiveAsync(user.Id)).Should().Be(0, "the race path revokes, as before");
    }

    [Fact]
    public async Task Two_retries_at_once_do_not_both_succeed()
    {
        for (var round = 0; round < 3; round++)
        {
            var user = await SeedUserAsync();
            var a1 = await LoginAsync(user);
            await RotateAsync(a1);

            var responses = await Task.WhenAll(RefreshAsync(a1), RefreshAsync(a1));

            responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "the window covers one retry");
            (await LiveAsync(user.Id)).Should().Be(0, "the request that lost revoked the user's sessions");
        }
    }

    // ---- the setting ---------------------------------------------------------------------

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [RefreshRetryWindow.Key] = value }).Build();

    [Theory]
    [InlineData(null, 30, 0)]
    [InlineData("45", 45, 0)]
    [InlineData("0", 0, 0)]
    [InlineData("300", 300, 0)]
    [InlineData("301", 300, 1)]
    [InlineData("99999", 300, 1)]
    [InlineData("-1", 0, 1)]
    [InlineData("99999999999999999999", 300, 1)]
    [InlineData("soon", 30, 1)]
    [InlineData("1.5", 30, 1)]
    public void The_window_is_clamped_and_each_change_is_reported(string? raw, int seconds, int warnings)
    {
        RefreshRetryWindow.Read(Config(raw), out var notes).Should().Be(TimeSpan.FromSeconds(seconds));
        notes.Should().HaveCount(warnings);
    }

    [Fact]
    public async Task A_clamped_value_is_logged_as_a_warning_at_start()
    {
        var logger = new CapturingLogger<RefreshRetryWindowCheck>();

        await new RefreshRetryWindowCheck(Config("9000"), logger).StartAsync(Ct);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain(RefreshRetryWindow.Key).And.Contain("300");

        var quiet = new CapturingLogger<RefreshRetryWindowCheck>();
        await new RefreshRetryWindowCheck(Config("20"), quiet).StartAsync(Ct);
        quiet.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void The_host_registers_the_startup_check()
    {
        factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<RefreshRetryWindowCheck>().Should().ContainSingle();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
