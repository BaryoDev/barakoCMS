using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Auth;

/// <summary>
/// List and end signed-in sessions (#1365): the caller's own through /api/me/sessions, and a global
/// SuperAdmin's for any user through /api/users/{id}/sessions.
/// </summary>
[Collection("Integration1")]
public class SessionRoutesTests
{
    private const string Password = "P@ssword123!Ab";
    private const string ChromeOnMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    private const string FirefoxOnLinux = "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0";

    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public SessionRoutesTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- helpers -------------------------------------------------------------------------

    private static string NextIp() => $"2001:db8:1365:{Interlocked.Increment(ref _ip):x}::1";

    private HttpClient Client(string? bearer = null, string? userAgent = null, string? ip = null)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? NextIp());
        if (userAgent is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    private async Task<(Guid Id, string Username)> SeedUserAsync(List<Guid>? roleIds = null)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        var username = $"routes-{id:N}";
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            RoleIds = roleIds ?? [],
        });
        await session.SaveChangesAsync(Ct);
        return (id, username);
    }

    private sealed record Signed(Guid UserId, string Username, string Token, string Sid);

    private async Task<Signed> NewUserSignedInAsync(string userAgent = ChromeOnMac, string? ip = null, List<Guid>? roleIds = null)
    {
        var (id, username) = await SeedUserAsync(roleIds);
        var token = await LoginAsync(username, userAgent, ip);
        return new Signed(id, username, token, Sid(token));
    }

    private async Task<string> LoginAsync(string username, string userAgent = ChromeOnMac, string? ip = null)
    {
        var response = await Client(userAgent: userAgent, ip: ip).PostAsJsonAsync(
            "/api/auth/login",
            new barakoCMS.Features.Auth.Login.Request { Username = username, Password = Password }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>(Ct))!.Token!;
    }

    private static string Sid(string jwt) =>
        new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(jwt)
            .Claims.First(c => c.Type == "sid").Value;

    private async Task<JsonElement> ListAsync(string token, string path = "/api/me/sessions")
    {
        var response = await Client(bearer: token).GetAsync(path, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<JsonElement> Items(JsonElement page) => page.GetProperty("items").EnumerateArray().ToList();

    private static List<string> Ids(JsonElement page) => Items(page).Select(i => i.GetProperty("id").GetString()!).ToList();

    private Task<HttpResponseMessage> Delete(string token, string path) => Client(bearer: token).DeleteAsync(path, Ct);

    private async Task<string> SuperAdminTokenAsync() => await _fixture.StoredUserTokenAsync("SuperAdmin");

    private async Task<string> StoreRowAsync(Guid userId, Guid? sessionId, DateTime expiresAt, bool revoked)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var raw = Guid.NewGuid().ToString("N");
        var row = sessionId is { } sid
            ? RefreshToken.StartSession(userId, raw, expiresAt, new DeviceContext(FirefoxOnLinux, NextIp(), null, "x"), "password", sid)
            : new RefreshToken { Id = Guid.NewGuid(), UserId = userId, TokenHash = RefreshToken.HashOf(raw), ExpiresAt = expiresAt };
        row.IsRevoked = revoked;
        session.Store(row);
        await session.SaveChangesAsync(Ct);
        return raw;
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refresh) =>
        await Client().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh }, Ct);

    /// <summary>A user who holds manage_users through a custom role, so the capability gate lets them in.</summary>
    private async Task<Signed> ManageUsersCallerAsync()
    {
        var roleId = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Role
            {
                Id = roleId,
                Name = $"users-{roleId:N}",
                Permissions = new(),
                SystemCapabilities = [SystemCapabilities.ManageUsers],
            });
            await session.SaveChangesAsync(Ct);
        }

        return await NewUserSignedInAsync(roleIds: [roleId]);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid targetUserId)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var id = targetUserId.ToString();
        return (await session.Query<AuditEvent>()
            .Where(e => e.Action == "auth.session.revoked" && e.TargetId == id)
            .ToListAsync(Ct)).ToList();
    }

    // ---- tests ---------------------------------------------------------------------------

    [Fact]
    public async Task List_shows_each_session_with_current_marked()
    {
        var firstIp = NextIp();
        var first = await NewUserSignedInAsync(ChromeOnMac, firstIp);
        await Task.Delay(50, Ct);
        var second = await LoginAsync(first.Username, FirefoxOnLinux);
        var secondSid = Sid(second);

        var page = await ListAsync(first.Token);

        var items = Items(page);
        items.Should().HaveCount(2);
        Ids(page).Should().Equal(new[] { secondSid, first.Sid }, "the session seen last comes first");
        items.Where(i => i.GetProperty("current").GetBoolean()).Should().ContainSingle()
            .Which.GetProperty("id").GetString().Should().Be(first.Sid);

        var mine = items.Single(i => i.GetProperty("id").GetString() == first.Sid);
        mine.GetProperty("device").GetString().Should().Contain("Chrome");
        mine.GetProperty("signInMethod").GetString().Should().Be("password");
        mine.GetProperty("signInIp").GetString().Should().Be(firstIp);
        mine.GetProperty("lastSeenIp").GetString().Should().Be(firstIp);
        mine.GetProperty("startedAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        mine.GetProperty("lastSeenAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        (await ListAsync(second)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("current").GetBoolean()).GetProperty("id").GetString().Should().Be(secondSid);
    }

    [Fact]
    public async Task List_leaves_out_ended_expired_and_unmarked_rows()
    {
        var live = await NewUserSignedInAsync();
        var ended = Sid(await LoginAsync(live.Username));
        (await Delete(live.Token, $"/api/me/sessions/{ended}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var revoked = Guid.NewGuid();
        var expired = Guid.NewGuid();
        await StoreRowAsync(live.UserId, revoked, DateTime.UtcNow.AddDays(1), revoked: true);
        await StoreRowAsync(live.UserId, expired, DateTime.UtcNow.AddMinutes(-1), revoked: false);
        await StoreRowAsync(live.UserId, null, DateTime.UtcNow.AddDays(1), revoked: false);

        var ids = Ids(await ListAsync(live.Token));

        ids.Should().Equal(new[] { live.Sid }, "only the live, unexpired row that has a session id is listed");
    }

    [Fact]
    public async Task List_is_paged_and_capped()
    {
        var user = await NewUserSignedInAsync();
        for (var i = 0; i < 4; i++)
        {
            await StoreRowAsync(user.UserId, Guid.NewGuid(), DateTime.UtcNow.AddDays(1), revoked: false);
        }

        var first = await ListAsync(user.Token, "/api/me/sessions?page=1&pageSize=2");
        var last = await ListAsync(user.Token, "/api/me/sessions?page=3&pageSize=2");
        var huge = await ListAsync(user.Token, "/api/me/sessions?pageSize=100000");

        Items(first).Should().HaveCount(2);
        first.GetProperty("totalItems").GetInt32().Should().Be(5);
        first.GetProperty("hasNextPage").GetBoolean().Should().BeTrue();
        Items(last).Should().HaveCount(1);
        huge.GetProperty("pageSize").GetInt32().Should().Be(PaginatedRequest.MaxPageSize);
        Ids(first).Concat(Ids(await ListAsync(user.Token, "/api/me/sessions?page=2&pageSize=2"))).Concat(Ids(last))
            .Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }

    [Fact]
    public async Task Ending_another_users_session_on_me_answers_404()
    {
        var mine = await NewUserSignedInAsync();
        var theirs = await NewUserSignedInAsync();

        (await Delete(mine.Token, $"/api/me/sessions/{theirs.Sid}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(mine.Token, $"/api/me/sessions/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Client(bearer: theirs.Token).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AuditAsync(theirs.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Ending_the_current_session_signs_the_caller_out()
    {
        var user = await NewUserSignedInAsync();

        (await Delete(user.Token, $"/api/me/sessions/{user.Sid}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client(bearer: user.Token).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_others_keeps_the_current_session()
    {
        var user = await NewUserSignedInAsync();
        var other = await LoginAsync(user.Username, FirefoxOnLinux);
        var third = await LoginAsync(user.Username, FirefoxOnLinux);

        (await Client(bearer: user.Token).PostAsync("/api/me/sessions/revoke-others", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        Ids(await ListAsync(user.Token)).Should().Equal(user.Sid);
        (await Client(bearer: other).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(bearer: third).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuditAsync(user.UserId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Admin_routes_need_a_global_super_admin()
    {
        var target = await NewUserSignedInAsync();
        var caller = await NewUserSignedInAsync();
        var admin = await _fixture.StoredUserTokenAsync("Admin");
        var usersManager = await ManageUsersCallerAsync();
        var superAdmin = await SuperAdminTokenAsync();

        var paths = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, $"/api/users/{target.UserId}/sessions"),
            (HttpMethod.Delete, $"/api/users/{target.UserId}/sessions/{target.Sid}"),
            (HttpMethod.Delete, $"/api/users/{target.UserId}/sessions"),
        };

        foreach (var (method, path) in paths)
        {
            (await Client().SendAsync(new HttpRequestMessage(method, path), Ct))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path} without a token");
            foreach (var token in new[] { caller.Token, admin, usersManager.Token })
            {
                var request = new HttpRequestMessage(method, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                (await Client().SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
            }
        }

        (await Client(bearer: target.Token).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(bearer: usersManager.Token).GetAsync("/api/users", Ct)).StatusCode.Should().Be(HttpStatusCode.OK,
            "the caller does pass the capability gate, so the 403s above come from the global SuperAdmin check");
        Ids(await ListAsync(superAdmin, $"/api/users/{target.UserId}/sessions")).Should().Equal(target.Sid);
        (await Client(bearer: superAdmin).GetAsync($"/api/users/{Guid.NewGuid()}/sessions", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(superAdmin, $"/api/users/{target.UserId}/sessions/{Guid.NewGuid()}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Delete(superAdmin, $"/api/users/{target.UserId}/sessions/{target.Sid}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Admin_end_all_ends_every_session_of_the_user()
    {
        var user = await NewUserSignedInAsync();
        var other = await LoginAsync(user.Username, FirefoxOnLinux);
        var superAdmin = await SuperAdminTokenAsync();
        Ids(await ListAsync(superAdmin, $"/api/users/{user.UserId}/sessions")).Should().HaveCount(2);

        (await Delete(superAdmin, $"/api/users/{user.UserId}/sessions")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client(bearer: user.Token).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(bearer: other).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        Items(await ListAsync(superAdmin, $"/api/users/{user.UserId}/sessions")).Should().BeEmpty();
    }

    [Fact]
    public async Task Each_end_is_audited_with_actor_target_and_session()
    {
        var user = await NewUserSignedInAsync();
        var second = Sid(await LoginAsync(user.Username, FirefoxOnLinux));
        var third = Sid(await LoginAsync(user.Username, FirefoxOnLinux));
        var superAdmin = await SuperAdminTokenAsync();
        var adminId = Guid.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(superAdmin).Claims.First(c => c.Type == "UserId").Value);

        (await Delete(user.Token, $"/api/me/sessions/{user.Sid}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Delete(superAdmin, $"/api/users/{user.UserId}/sessions/{second}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Delete(superAdmin, $"/api/users/{user.UserId}/sessions")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var events = await AuditAsync(user.UserId);
        events.Should().HaveCount(3);
        events.Select(e => e.Metadata!["sessionId"].ToString()).Should().BeEquivalentTo([user.Sid, second, third]);
        events.Single(e => e.Metadata!["sessionId"].ToString() == user.Sid).ActorUserId.Should().Be(user.UserId);
        events.Where(e => e.Metadata!["sessionId"].ToString() != user.Sid).Should().OnlyContain(e => e.ActorUserId == adminId);
        events.Should().OnlyContain(e => e.TargetType == "User" && e.IpAddress == null);
        JsonSerializer.Serialize(events.Select(e => e.Metadata)).Should().NotContain("Chrome").And.NotContain("2001:db8");
    }

    [Fact]
    public async Task Ending_an_already_ended_session_answers_404()
    {
        var user = await NewUserSignedInAsync();
        var other = Sid(await LoginAsync(user.Username, FirefoxOnLinux));

        (await Delete(user.Token, $"/api/me/sessions/{other}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Delete(user.Token, $"/api/me/sessions/{other}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await AuditAsync(user.UserId)).Should().ContainSingle("only the call that ended it audits it");
    }

    [Fact]
    public async Task Revoke_others_from_a_token_with_no_session_ends_every_session()
    {
        var user = await NewUserSignedInAsync();
        var other = await LoginAsync(user.Username, FirefoxOnLinux);
        var noSid = _fixture.CreateToken(["User"], user.UserId.ToString());

        (await Client(bearer: noSid).PostAsync("/api/me/sessions/revoke-others", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Client(bearer: user.Token).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client(bearer: other).GetAsync("/api/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task End_all_also_revokes_rows_that_have_no_session_id()
    {
        var viaAdmin = await NewUserSignedInAsync();
        var adminRaw = await StoreRowAsync(viaAdmin.UserId, null, DateTime.UtcNow.AddDays(1), revoked: false);
        var viaOthers = await NewUserSignedInAsync();
        var othersRaw = await StoreRowAsync(viaOthers.UserId, null, DateTime.UtcNow.AddDays(1), revoked: false);

        (await Delete(await SuperAdminTokenAsync(), $"/api/users/{viaAdmin.UserId}/sessions")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Client(bearer: viaOthers.Token).PostAsync("/api/me/sessions/revoke-others", null, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RefreshAsync(adminRaw)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await RefreshAsync(othersRaw)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task End_all_with_only_rows_that_have_no_session_id_revokes_them()
    {
        var id = (await SeedUserAsync()).Id;
        var raw = await StoreRowAsync(id, null, DateTime.UtcNow.AddDays(1), revoked: false);

        (await Delete(await SuperAdminTokenAsync(), $"/api/users/{id}/sessions")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RefreshAsync(raw)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ending_the_current_session_clears_the_refresh_cookie()
    {
        var user = await NewUserSignedInAsync();

        var response = await Delete(user.Token, $"/api/me/sessions/{user.Sid}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }
}
