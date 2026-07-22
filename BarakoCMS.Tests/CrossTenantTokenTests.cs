using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Lets a test choose its own client IP.
///
/// The auth endpoints rate-limit to 5 attempts per 15 minutes partitioned by remote IP, and under
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/> every request arrives with
/// no remote IP — so all tests share a single bucket and later ones get 429s. That is worse than
/// flakiness: a test asserting "this request must fail" passes when the request was merely
/// rate-limited, which is a green test proving nothing.
///
/// Registered as an <see cref="IStartupFilter"/> so it runs ahead of the rate limiter.
/// </summary>
internal sealed class TestRemoteIpFilter : IStartupFilter
{
    public const string Header = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nxt) =>
        {
            if (ctx.Request.Headers.TryGetValue(Header, out var raw)
                && IPAddress.TryParse(raw.ToString(), out var ip))
            {
                ctx.Connection.RemoteIpAddress = ip;
            }
            await nxt();
        });
        next(app);
    };
}

/// <summary>
/// Regression cover for the cross-tenant token gap (H.1).
///
/// The tenant a token is scoped to comes from the client-supplied <c>X-Tenant</c> header. Login,
/// OTP verify and refresh all trusted it and minted a matching <c>tenant</c> claim without checking
/// membership; only <c>/api/me/switch</c> checked. Role resolution falls back to a user's global
/// roles when no membership exists, so any registered user could authenticate against any tenant
/// and receive a working token for it.
///
/// These tests drive real HTTP so they cover the middleware chain as deployed, not just the issuer.
/// </summary>
[Collection("Sequential")]
public class CrossTenantTokenTests
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _factory;

    /// <summary>Distinct per test instance, so each gets its own rate-limit bucket.</summary>
    private readonly string _clientIp;

    private static int _ipCounter;

    private const string Password = "P@ssword123!";

    public CrossTenantTokenTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        var n = Interlocked.Increment(ref _ipCounter);
        _clientIp = $"203.0.113.{n % 250 + 1}"; // TEST-NET-3, reserved for documentation
        _client = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<IStartupFilter, TestRemoteIpFilter>()))
            .CreateClient();
    }

    private async Task<(Guid Id, string Username)> CreateUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var username = $"xtenant-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        session.Store(new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@test.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
            // Global roles are the crux: they are what the old code fell back to, which is how a
            // non-member ended up with a privileged token in someone else's tenant.
            RoleIds = new List<Guid> { Guid.Parse("00000000-0000-0000-0000-000000000001") },
        });
        await session.SaveChangesAsync();
        return (id, username);
    }

    private async Task<string> CreateTenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var slug = $"club-{Guid.NewGuid():N}".ToLowerInvariant();
        session.Store(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = slug,
            IsActive = true,
        });
        await session.SaveChangesAsync();
        return slug;
    }

    private async Task GrantMembershipAsync(Guid userId, string tenantSlug)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TenantSlug = tenantSlug,
            Status = MembershipStatus.Active,
            RoleIds = new List<Guid>(),
        });
        await session.SaveChangesAsync();
    }

    private HttpRequestMessage Post(string url, object body, string? tenant = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add(TestRemoteIpFilter.Header, _clientIp);
        if (tenant is not null)
            req.Headers.Add("X-Tenant", tenant);
        return req;
    }

    private static string? TenantClaimOf(string jwt) =>
        new JwtSecurityTokenHandler().ReadJwtToken(jwt).Claims
            .FirstOrDefault(c => c.Type == "tenant")?.Value;

    /// <summary>
    /// Asserts a request was rejected *on its merits*.
    ///
    /// Plain <c>IsSuccessStatusCode.Should().BeFalse()</c> is not good enough here: a 429 from the
    /// auth rate limiter also satisfies it, so the test would pass without the authorization check
    /// ever running. That exact false pass hid a real hole during development of this fix.
    /// </summary>
    private static void ShouldBeRejectedOnMerits(HttpResponseMessage resp, string because)
    {
        resp.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
            "a rate-limited response proves nothing about authorization — the test is not exercising what it claims");
        resp.IsSuccessStatusCode.Should().BeFalse(because);
    }

    /// <summary>The original exploit: valid credentials plus an arbitrary X-Tenant.</summary>
    [Fact]
    public async Task login_with_an_unrelated_tenant_header_is_refused()
    {
        var (_, username) = await CreateUserAsync();
        var someoneElsesClub = await CreateTenantAsync();

        var resp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, someoneElsesClub));

        ShouldBeRejectedOnMerits(resp,
            "credentials prove identity, not membership — a non-member must not receive a token for this club");
    }

    [Fact]
    public async Task login_succeeds_for_a_tenant_the_user_actually_belongs_to()
    {
        var (userId, username) = await CreateUserAsync();
        var club = await CreateTenantAsync();
        await GrantMembershipAsync(userId, club);

        var resp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, club));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();
        TenantClaimOf(body!.Token!).Should().Be(club, "the token must be scoped to the club that was requested");
    }

    /// <summary>
    /// The default tenant has no Membership rows by design — it is the single-tenant/global context.
    /// Requiring membership there would lock out every non-multi-tenant deployment.
    /// </summary>
    [Fact]
    public async Task login_without_a_tenant_header_still_works()
    {
        var (_, username) = await CreateUserAsync();

        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password });

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();
        TenantClaimOf(body!.Token!).Should().Be(Tenant.DefaultSlug);
    }

    /// <summary>
    /// Refresh is the subtler hole: obtain a legitimate refresh token on one tenant, then present it
    /// with a different X-Tenant and walk out scoped to a tenant you never joined.
    /// </summary>
    [Fact]
    public async Task refresh_cannot_be_used_to_hop_to_another_tenant()
    {
        var (userId, username) = await CreateUserAsync();
        var ownClub = await CreateTenantAsync();
        var otherClub = await CreateTenantAsync();
        await GrantMembershipAsync(userId, ownClub);

        var loginResp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, ownClub));
        loginResp.EnsureSuccessStatusCode();
        var login = await loginResp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();

        var hop = await _client.SendAsync(
            Post("/api/auth/refresh", new { RefreshToken = login!.RefreshToken }, otherClub));

        ShouldBeRejectedOnMerits(hop,
            "a refresh token issued for one club must not mint a token for another");
    }

    [Fact]
    public async Task refresh_still_works_on_the_tenant_it_was_issued_for()
    {
        var (userId, username) = await CreateUserAsync();
        var club = await CreateTenantAsync();
        await GrantMembershipAsync(userId, club);

        var loginResp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, club));
        loginResp.EnsureSuccessStatusCode();
        var login = await loginResp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();

        var refreshResp = await _client.SendAsync(
            Post("/api/auth/refresh", new { RefreshToken = login!.RefreshToken }, club));

        refreshResp.EnsureSuccessStatusCode();
        var refreshed = await refreshResp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Refresh.Response>();
        TenantClaimOf(refreshed!.Token!).Should().Be(club);
    }

    /// <summary>
    /// Revoking a membership must take effect at the next refresh rather than lingering until the
    /// refresh token expires — otherwise removing someone leaves them with up to a week of access.
    /// </summary>
    [Fact]
    public async Task refresh_stops_working_once_membership_is_revoked()
    {
        var (userId, username) = await CreateUserAsync();
        var club = await CreateTenantAsync();
        await GrantMembershipAsync(userId, club);

        var loginResp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, club));
        loginResp.EnsureSuccessStatusCode();
        var login = await loginResp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var membership = await session.Query<Membership>()
                .FirstAsync(m => m.UserId == userId && m.TenantSlug == club);
            membership.Status = MembershipStatus.Removed;
            session.Update(membership);
            await session.SaveChangesAsync();
        }

        var resp = await _client.SendAsync(
            Post("/api/auth/refresh", new { RefreshToken = login!.RefreshToken }, club));

        ShouldBeRejectedOnMerits(resp, "a removed member must not be able to refresh into the club");
    }

    /// <summary>
    /// A slug with no Tenant document is not a managed tenant, so there is no membership model to
    /// enforce and login must still work.
    ///
    /// This is the ordinary shape of a single-tenant deployment reached over a subdomain:
    /// TenantResolutionMiddleware derives a slug from the host, nobody ever created a Tenant
    /// document, and every user legitimately works in that partition.
    ///
    /// The first version of this fix required a registered Tenant and took the public playground
    /// offline on deploy — every login there resolves to the slug "playground", which has no Tenant
    /// document. The original tests all used registered tenants or the default slug, so none of
    /// them caught it.
    /// </summary>
    [Fact]
    public async Task login_on_an_unregistered_tenant_slug_still_works()
    {
        var (_, username) = await CreateUserAsync();
        var unregistered = $"subdomain-{Guid.NewGuid():N}";

        var resp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, unregistered));

        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<barakoCMS.Features.Auth.Login.Response>();
        TenantClaimOf(body!.Token!).Should().Be(unregistered);
    }

    [Fact]
    public async Task login_on_a_registered_but_inactive_tenant_is_refused()
    {
        var (userId, username) = await CreateUserAsync();
        var club = await CreateTenantAsync();
        await GrantMembershipAsync(userId, club);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var tenant = await session.Query<Tenant>().FirstAsync(t => t.Slug == club);
            tenant.IsActive = false;
            session.Update(tenant);
            await session.SaveChangesAsync();
        }

        var resp = await _client.SendAsync(
            Post("/api/auth/login", new { Username = username, Password }, club));

        ShouldBeRejectedOnMerits(resp, "a deactivated club must not issue tokens even to its members");
    }
}
